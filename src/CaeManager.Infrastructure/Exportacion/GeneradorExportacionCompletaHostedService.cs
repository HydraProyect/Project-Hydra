using CaeManager.Application.Common;
using CaeManager.Application.Exportacion;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Exportacion;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Exportacion;

/// <summary>
/// Cola durable de las exportaciones completas en autoservicio (#2008, D-8), con el patrón de
/// <c>ProcesadorAnalisisDocumentoHostedService</c>: un solo proceso líder, un Tenant cada vez bajo
/// <see cref="AmbitoTenantExplicito"/> y el contexto de runtime sometido a RLS, nunca el rol de plataforma ni una
/// consulta que cruce Tenants. En cada vuelta y por cada Tenant propietario:
/// <list type="number">
/// <item>Da por fallida la solicitud que lleva más de <see cref="UmbralEstancada"/> en curso (un proceso que murió a
/// mitad), y borra su contraseña.</item>
/// <item>Barre las caducadas: borra el fichero y la marca <see cref="EstadoSolicitudExportacionCompleta.Caducada"/>.</item>
/// <item>Barre los temporales y zips huérfanos de la carpeta del Tenant (<see cref="AlmacenExportacionesCompletasEnDisco.BarrerHuerfanos"/>).</item>
/// <item>Reclama la pendiente (FOR UPDATE SKIP LOCKED), comprueba que el volumen tiene el espacio libre mínimo
/// (si no, la da por fallida con <see cref="MotivoSinEspacio"/>), genera el zip con el núcleo de
/// <see cref="ExportacionCompletaTenant"/> y, en un solo guardado, la marca lista, caduca la lista anterior del
/// Tenant, borra la contraseña y escribe sus filas de auditoría; después borra el fichero de la anterior.</item>
/// <item>Solo después de intentar reclamarla, da por fallida la pendiente que sigue sin reclamar y se pidió hace
/// más de <see cref="UmbralPendienteSinReclamar"/>: el líder recorre los Tenants en serie, y lo que una pendiente
/// espera a que le llegue el turno no cuenta como estancada.</item>
/// </list>
/// Consumo de disco acotado (OWASP API4:2023, ASVS 4.0.3 V12.1): como mucho un zip listo y un temporal por Tenant.
/// <para>
/// Dos procesos pueden creerse líderes a la vez (el anterior perdió la conexión que retenía el liderazgo y su
/// generación sigue). Por eso cada transición de estado se guarda con el estado leído como token de concurrencia
/// (ASVS 4.0.3 V1.11.2 y V11.1.6): gana el primer guardado, el segundo no se aplica ni se audita, y un fichero solo se
/// borra después de confirmar la transición que lo deja sin fila que lo nombre (salvo el de una caducada, que no
/// tiene otro destino posible).
/// </para>
/// </summary>
public class GeneradorExportacionCompletaHostedService(
    IServiceScopeFactory ambitoFactory,
    IEleccionLiderService eleccionLider,
    TimeProvider reloj,
    ILogger<GeneradorExportacionCompletaHostedService> logger) : BackgroundService
{
    public const string NombreLider = "generador-exportacion-completa";

    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromMinutes(1);

    /// <summary>Más de lo que tarda la exportación del Tenant más grande previsto; pasado esto, el proceso murió.</summary>
    public static readonly TimeSpan UmbralEstancada = TimeSpan.FromHours(3);

    /// <summary>
    /// Lo que una pendiente puede seguir sin reclamarse, contado desde que se pidió, antes de darla por fallida para
    /// que no guarde la contraseña indefinidamente. Solo se mira después del intento de reclamarla de la vuelta, así
    /// que la espera en la cola en serie no la hace fallar; las 24 h son el mismo intervalo que el límite de una
    /// solicitud cada 24 h.
    /// </summary>
    public static readonly TimeSpan UmbralPendienteSinReclamar = SolicitudExportacionCompleta.IntervaloMinimo;

    public const string MotivoEstancada = "Estancada";
    public const string MotivoSinEspacio = "SinEspacio";
    public const string MotivoInterrumpida = "Interrumpida";
    public const string MotivoGeneracionFallida = "GeneracionFallida";
    public const string MotivoSustituida = "Sustituida";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        using var temporizador = new PeriodicTimer(IntervaloSondeo);
        do
        {
            try
            {
                await eleccionLider.IntentarEjecutarComoLiderAsync(NombreLider, ProcesarTodosLosTenantsAsync, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falló un ciclo de la cola de exportaciones completas.");
            }
        }
        while (await temporizador.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProcesarTodosLosTenantsAsync(CancellationToken stoppingToken)
    {
        List<Guid> tenants;
        using (var ambito = ambitoFactory.CreateScope())
        {
            tenants = await ambito.ServiceProvider.GetRequiredService<ITenantsQueryContext>()
                .Tenants.Where(t => !t.EsPlataforma)
                .Select(t => t.Id)
                .ToListAsync(stoppingToken);
        }

        foreach (var tenantId in tenants)
        {
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                await ProcesarTenantAsync(tenantId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Falló la cola de exportaciones completas del Tenant {TenantId}; se sigue con el resto.", tenantId);
            }
        }
    }

    /// <summary>
    /// Una vuelta sobre un Tenant: en curso estancadas, caducadas, huérfanos, la pendiente y, tras intentar
    /// reclamarla, la pendiente que no se pudo reclamar. Público para las pruebas de integración.
    /// </summary>
    public async Task ProcesarTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        using var _ = AmbitoTenantExplicito.Establecer(tenantId);

        await FallarEnCursoEstancadasAsync(cancellationToken);
        try
        {
            await CaducarVencidasAsync(tenantId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Un barrido que falla (p. ej. un fichero que no se deja borrar) no bloquea la generación de este Tenant:
            // la fila sigue Lista, la fecha de caducidad ya impide descargarla y la próxima vuelta lo reintenta.
            logger.LogError(ex, "Falló el barrido de exportaciones caducadas del Tenant {TenantId}.", tenantId);
        }
        try
        {
            await BarrerHuerfanosAsync(tenantId, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Solo el tipo: el mensaje de System.IO lleva la ruta absoluta del almacén.
            logger.LogError("Falló el barrido de ficheros huérfanos de exportación del Tenant {TenantId} ({TipoError}).",
                tenantId, ex.GetType().Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // La consulta bajo RLS u otro fallo que no es de disco: la excepción entera, para el diagnóstico.
            logger.LogError(ex, "Falló el barrido de ficheros huérfanos de exportación del Tenant {TenantId}.", tenantId);
        }
        try
        {
            await GenerarPendienteAsync(tenantId, cancellationToken);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // Falló reclamar la pendiente, o marcarla fallida: la próxima vuelta lo reintenta, y si sigue sin
            // reclamarse pasado el umbral, la da por fallida el paso siguiente.
            logger.LogError(ex, "Falló la generación de la exportación pendiente del Tenant {TenantId}.", tenantId);
        }
        await FallarPendientesSinReclamarAsync(cancellationToken);
    }

    /// <summary>Una en curso sin terminar durante el umbral: el proceso que la generaba murió a mitad.</summary>
    private async Task FallarEnCursoEstancadasAsync(CancellationToken cancellationToken)
    {
        var limite = Ahora() - UmbralEstancada;
        await FallarComoEstancadasAsync(
            s => s.Estado == EstadoSolicitudExportacionCompleta.EnCurso && s.IniciadaEnUtc < limite, cancellationToken);
    }

    /// <summary>
    /// Una pendiente que sigue sin reclamar después del intento de esta vuelta y se pidió hace más de
    /// <see cref="UmbralPendienteSinReclamar"/>: se da por fallida para que no guarde la contraseña indefinidamente
    /// ni ocupe el índice de solicitud viva del Tenant.
    /// </summary>
    private async Task FallarPendientesSinReclamarAsync(CancellationToken cancellationToken)
    {
        var limite = Ahora() - UmbralPendienteSinReclamar;
        await FallarComoEstancadasAsync(
            s => s.Estado == EstadoSolicitudExportacionCompleta.Pendiente && s.SolicitadaEnUtc < limite, cancellationToken);
    }

    private async Task FallarComoEstancadasAsync(
        System.Linq.Expressions.Expression<Func<SolicitudExportacionCompleta, bool>> criterio, CancellationToken cancellationToken)
    {
        using var ambito = ambitoFactory.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var ahora = Ahora();

        var estancadas = await contexto.SolicitudesExportacionCompleta.Where(criterio).ToListAsync(cancellationToken);

        // Una a una, cada transición con su auditoría en su propio guardado: si otro proceso la cambió desde que se
        // leyó (p. ej. el líder anterior acaba de marcarla Lista), esa no se aplica ni se audita y las demás siguen.
        foreach (var solicitud in estancadas)
        {
            SeguirSiSeDescarto(contexto, solicitud);
            solicitud.MarcarFallida(MotivoEstancada, ahora);
            contexto.RegistrosAuditoria.Add(AuditoriaExportacionCompleta.DelSistema(
                solicitud.Id, AuditoriaExportacionCompleta.FaseFallida, new { motivo = MotivoEstancada }));
            await GuardarTransicionAsync(contexto, solicitud.Id, cancellationToken);
        }
    }

    /// <summary>
    /// Guarda una transición de estado junto con su fila de auditoría. El estado es el token de concurrencia de la
    /// fila (<c>SolicitudExportacionCompletaConfiguration</c>; ASVS 4.0.3 V1.11.2 y V11.1.6): si otro proceso lo
    /// cambió desde que se leyó, el UPDATE afecta a cero filas, el guardado entero (transición y auditoría) se revierte
    /// y se descarta lo pendiente del contexto. Devuelve si la transición se aplicó.
    /// </summary>
    private async Task<bool> GuardarTransicionAsync(CaeManagerDbContext contexto, Guid solicitudId, CancellationToken cancellationToken)
    {
        try
        {
            await contexto.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            contexto.ChangeTracker.Clear();
            logger.LogWarning(
                "La exportación completa {SolicitudId} cambió de estado en otro proceso; esta transición no se aplica ni se audita.",
                solicitudId);
            return false;
        }
    }

    /// <summary>
    /// Tras un conflicto, <see cref="GuardarTransicionAsync"/> vacía el contexto: la siguiente fila leída en la misma
    /// consulta se vuelve a seguir con los valores que se leyeron, que son los que irán en el WHERE de su UPDATE.
    /// </summary>
    private static void SeguirSiSeDescarto(CaeManagerDbContext contexto, SolicitudExportacionCompleta solicitud)
    {
        if (contexto.Entry(solicitud).State == EntityState.Detached)
            contexto.Attach(solicitud);
    }

    /// <summary>
    /// Los zips de las listas y las ubicaciones de las vivas se conservan; el resto de la carpeta del Tenant que nadie
    /// toca desde hace <see cref="UmbralEstancada"/> es de un intento roto y se borra.
    /// </summary>
    private async Task BarrerHuerfanosAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        using var ambito = ambitoFactory.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var almacen = ambito.ServiceProvider.GetRequiredService<AlmacenExportacionesCompletasEnDisco>();

        var conservar = await contexto.SolicitudesExportacionCompleta
            .Where(s => s.Estado == EstadoSolicitudExportacionCompleta.Lista
                || s.Estado == EstadoSolicitudExportacionCompleta.Pendiente
                || s.Estado == EstadoSolicitudExportacionCompleta.EnCurso)
            .Select(s => new { s.Id, s.UbicacionFichero })
            .ToListAsync(cancellationToken);
        var vigentes = conservar
            .Select(s => s.UbicacionFichero ?? AlmacenExportacionesCompletasEnDisco.UbicacionPara(tenantId, s.Id))
            .ToHashSet(StringComparer.Ordinal);

        var borrados = almacen.BarrerHuerfanos(tenantId, vigentes, Ahora() - UmbralEstancada);
        if (borrados > 0)
            logger.LogWarning("Se borraron {Borrados} ficheros huérfanos de exportación del Tenant {TenantId}.", borrados, tenantId);
    }

    private async Task CaducarVencidasAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        using var ambito = ambitoFactory.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var almacen = ambito.ServiceProvider.GetRequiredService<AlmacenExportacionesCompletasEnDisco>();
        var ahora = Ahora();

        var vencidas = await contexto.SolicitudesExportacionCompleta
            .Where(s => s.Estado == EstadoSolicitudExportacionCompleta.Lista && s.CaducaEnUtc <= ahora)
            .ToListAsync(cancellationToken);

        foreach (var solicitud in vencidas)
        {
            SeguirSiSeDescarto(contexto, solicitud);
            // Primero el fichero: si el borrado falla, la fila sigue Lista y la próxima vuelta lo reintenta; la
            // descarga ya la impide la fecha de caducidad. Si el guardado choca con otro proceso, este solo pudo
            // caducarla también (de Lista no se sale a otro estado), así que el fichero borrado ya no lo nombra nadie.
            if (solicitud.UbicacionFichero is { } ubicacion)
                almacen.Eliminar(tenantId, ubicacion);

            solicitud.MarcarCaducada(ahora);
            contexto.RegistrosAuditoria.Add(AuditoriaExportacionCompleta.DelSistema(
                solicitud.Id, AuditoriaExportacionCompleta.FaseCaducada));
            await GuardarTransicionAsync(contexto, solicitud.Id, cancellationToken);
        }
    }

    private async Task GenerarPendienteAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        Guid? solicitudId;
        using (var ambito = ambitoFactory.CreateScope())
            solicitudId = await ReclamarPendienteAsync(ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>(), cancellationToken);

        if (solicitudId is not { } id)
            return;

        string? ubicacionConfirmada = null;
        var ficherosSustituidos = new List<string?>();
        try
        {
            using var ambito = ambitoFactory.CreateScope();
            var proveedor = ambito.ServiceProvider;
            var contexto = proveedor.GetRequiredService<CaeManagerDbContext>();
            var almacen = proveedor.GetRequiredService<AlmacenExportacionesCompletasEnDisco>();
            var documentos = proveedor.GetRequiredService<IFileStorageService>();
            var protector = proveedor.GetRequiredService<IProtectorContrasenaExportacion>();

            // OWASP API4:2023 y ASVS 4.0.3 V12.1: no se empieza un zip que pueda llenar el volumen que comparte con
            // los Documentos. El registro da solo las cifras de espacio, sin datos del Tenant.
            if (!almacen.HayEspacioParaGenerar(out var libreBytes))
            {
                logger.LogWarning(
                    "Exportación completa {SolicitudId} no generada por falta de espacio: {LibreMiB} MiB libres en el volumen del almacén y el mínimo es {MinimoMiB} MiB.",
                    id, libreBytes / (1024 * 1024), almacen.EspacioLibreMinimoBytes / (1024 * 1024));
                await FallarAsync(tenantId, id, MotivoSinEspacio, ubicacionConfirmada: null);
                return;
            }

            var solicitud = await contexto.SolicitudesExportacionCompleta.SingleAsync(s => s.Id == id, cancellationToken);
            var contrasena = protector.Desproteger(solicitud.ContrasenaProtegida
                ?? throw new InvalidOperationException("La solicitud en curso no tiene contraseña."));

            var ubicacion = AlmacenExportacionesCompletasEnDisco.UbicacionPara(tenantId, id);
            ExportacionCompletaTenant.Resultado? resultado = null;
            long tamano = 0;

            // La foto de la base es una transacción explícita: va dentro de la estrategia de ejecución, y cada
            // intento escribe su propio temporal desde cero.
            await contexto.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                var (flujo, temporal) = almacen.CrearTemporal(tenantId, id);
                try
                {
                    await using (flujo)
                        resultado = await ExportacionCompletaTenant.GenerarParaSolicitudAsync(
                            contexto, documentos, tenantId, contrasena, flujo, cancellationToken);
                    tamano = almacen.Confirmar(tenantId, ubicacion, temporal);
                }
                catch
                {
                    AlmacenExportacionesCompletasEnDisco.DescartarTemporal(temporal);
                    throw;
                }
            });
            ubicacionConfirmada = ubicacion;

            // El estado es el token de concurrencia (ASVS 4.0.3 V1.11.2 y V11.1.6): si otro proceso dio esta por
            // fallida (estancada) desde que se leyó, el guardado choca, nada de él se aplica ni se audita, y el catch
            // la encuentra ya terminal y no la toca. Si con lo que chocó es con una anterior (que otro proceso caducó
            // entretanto), se vuelve a leer y se reintenta una vez: la exportación generada no se pierde por eso.
            for (var intento = 1; ; intento++)
            {
                contexto.ChangeTracker.Clear();
                ficherosSustituidos.Clear();
                var ahora = Ahora();
                var lista = await contexto.SolicitudesExportacionCompleta.SingleAsync(s => s.Id == id, cancellationToken);
                // Como mucho una lista viva por Tenant (OWASP API4:2023, ASVS 4.0.3 V12.1): la anterior caduca en el
                // mismo guardado que marca esta, y su fichero se borra después; si ese borrado falla, lo recoge el
                // barrido de huérfanos, porque ninguna fila lista lo nombra ya.
                var anteriores = await contexto.SolicitudesExportacionCompleta
                    .Where(s => s.Estado == EstadoSolicitudExportacionCompleta.Lista && s.Id != id)
                    .ToListAsync(cancellationToken);
                lista.MarcarLista(ubicacion, tamano, ahora);
                contexto.RegistrosAuditoria.Add(AuditoriaExportacionCompleta.DelSistema(
                    id, AuditoriaExportacionCompleta.FaseLista,
                    new { tamanoBytes = tamano, recuentos = resultado!.Recuentos, documentosSinFichero = resultado.DocumentosSinFichero }));
                foreach (var anterior in anteriores)
                {
                    ficherosSustituidos.Add(anterior.UbicacionFichero);
                    anterior.CaducarPorSustitucion(ahora);
                    contexto.RegistrosAuditoria.Add(AuditoriaExportacionCompleta.DelSistema(
                        anterior.Id, AuditoriaExportacionCompleta.FaseCaducada, new { motivo = MotivoSustituida, sustituidaPor = id }));
                }

                try
                {
                    await contexto.SaveChangesAsync(cancellationToken);
                    break;
                }
                catch (DbUpdateConcurrencyException ex) when (intento == 1 && !ex.Entries.Any(e => ReferenceEquals(e.Entity, lista)))
                {
                    logger.LogWarning(
                        "Una exportación anterior del Tenant {TenantId} cambió de estado en otro proceso mientras se marcaba lista la {SolicitudId}; se reintenta.",
                        tenantId, id);
                }
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            // Lo más probable: la solicitud ya no estaba en curso porque otro proceso la terminó (p. ej. el barrido de
            // estancadas de un líder nuevo). Esa transición es la que vale; esta no se aplica ni se audita, y
            // FallarAsync la encuentra terminal y no la toca. Si fue el segundo choque seguido con una anterior, la
            // solicitud sigue en curso y FallarAsync la da por fallida como cualquier otro fallo de generación.
            ficherosSustituidos.Clear();
            logger.LogWarning(
                "La exportación completa {SolicitudId} del Tenant {TenantId} chocó con otro proceso al marcarse lista; no se marca.",
                id, tenantId);
            await FallarAsync(tenantId, id, MotivoGeneracionFallida, ubicacionConfirmada);
            return;
        }
        catch (Exception ex)
        {
            var motivo = ex is OperationCanceledException && cancellationToken.IsCancellationRequested ? MotivoInterrumpida : MotivoGeneracionFallida;
            logger.LogError(ex, "Falló la exportación completa {SolicitudId} del Tenant {TenantId}.", id, tenantId);
            await FallarAsync(tenantId, id, motivo, ubicacionConfirmada);
            if (motivo == MotivoInterrumpida)
                throw;
            return;
        }

        BorrarSinBloquear(tenantId, ficherosSustituidos);
    }

    /// <summary>
    /// Borra ficheros de exportaciones cuya fila ya no los nombra (sustituidas o fallidas). Un borrado que falla no
    /// detiene nada: lo recoge el barrido de huérfanos.
    /// </summary>
    private void BorrarSinBloquear(Guid tenantId, IEnumerable<string?> ubicaciones)
    {
        using var ambito = ambitoFactory.CreateScope();
        var almacen = ambito.ServiceProvider.GetRequiredService<AlmacenExportacionesCompletasEnDisco>();
        foreach (var ubicacion in ubicaciones.OfType<string>())
        {
            try
            {
                almacen.Eliminar(tenantId, ubicacion);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Solo el tipo: el mensaje de System.IO lleva la ruta absoluta del fichero.
                logger.LogWarning("No se pudo borrar el fichero de una exportación sustituida o fallida del Tenant {TenantId} ({TipoError}); lo recogerá el barrido de huérfanos.",
                    tenantId, ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Marca fallida en un contexto nuevo, sin el token de parada: un apagado a mitad no debe dejar la contraseña
    /// guardada ni la solicitud en curso hasta el umbral de estancadas.
    /// </summary>
    private async Task FallarAsync(Guid tenantId, Guid id, string motivo, string? ubicacionConfirmada)
    {
        using var ambito = ambitoFactory.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        // Primero el estado: si el guardado de Lista llegó a confirmarse antes de la excepción, la fila ya es Lista y
        // su fichero es el que se descarga; borrarlo dejaría una exportación entregada sin fichero.
        var solicitud = await contexto.SolicitudesExportacionCompleta.SingleOrDefaultAsync(s => s.Id == id, CancellationToken.None);
        if (solicitud?.Estado is not (EstadoSolicitudExportacionCompleta.Pendiente or EstadoSolicitudExportacionCompleta.EnCurso))
            return;

        solicitud.MarcarFallida(motivo, Ahora());
        contexto.RegistrosAuditoria.Add(AuditoriaExportacionCompleta.DelSistema(
            id, AuditoriaExportacionCompleta.FaseFallida, new { motivo }));
        // El fichero, solo después de confirmar la transición (ASVS 4.0.3 V11.1.6): si otro proceso cambió la fila
        // entre la lectura y el guardado, la transición no se aplica ni se audita y el fichero no se toca.
        if (!await GuardarTransicionAsync(contexto, id, CancellationToken.None))
            return;

        if (ubicacionConfirmada is not null)
            BorrarSinBloquear(tenantId, [ubicacionConfirmada]);
    }

    /// <summary>Pasa a EnCurso la pendiente más antigua del Tenant en curso, con una transacción corta.</summary>
    private Task<Guid?> ReclamarPendienteAsync(CaeManagerDbContext contexto, CancellationToken cancellationToken) =>
        contexto.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaccion = await contexto.Database.BeginTransactionAsync(cancellationToken);

            var solicitud = await contexto.SolicitudesExportacionCompleta
                .FromSqlInterpolated($"""
                    SELECT * FROM "SolicitudesExportacionCompleta"
                    WHERE "Estado" = 'Pendiente'
                    ORDER BY "SolicitadaEnUtc"
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                    """)
                .SingleOrDefaultAsync(cancellationToken);

            if (solicitud is null)
            {
                await transaccion.CommitAsync(cancellationToken);
                return (Guid?)null;
            }

            solicitud.MarcarEnCurso(Ahora());
            await contexto.SaveChangesAsync(cancellationToken);
            await transaccion.CommitAsync(cancellationToken);
            contexto.ChangeTracker.Clear();
            return solicitud.Id;
        });

    private DateTime Ahora() => reloj.GetUtcNow().UtcDateTime;
}
