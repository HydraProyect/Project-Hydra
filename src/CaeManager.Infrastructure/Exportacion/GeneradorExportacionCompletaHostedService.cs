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
/// <item>Reclama la pendiente (FOR UPDATE SKIP LOCKED), genera el zip con el núcleo de
/// <see cref="ExportacionCompletaTenant"/> y, en un solo guardado, la marca lista o fallida, borra la contraseña y
/// escribe su fila de auditoría.</item>
/// </list>
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

    /// <summary>Una vuelta sobre un Tenant: estancadas, caducadas y la pendiente. Público para las pruebas de integración.</summary>
    public async Task ProcesarTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        using var _ = AmbitoTenantExplicito.Establecer(tenantId);

        await FallarEstancadasAsync(cancellationToken);
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
        await GenerarPendienteAsync(tenantId, cancellationToken);
    }

    private async Task FallarEstancadasAsync(CancellationToken cancellationToken)
    {
        using var ambito = ambitoFactory.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var ahora = Ahora();
        var limite = ahora - UmbralEstancada;

        var estancadas = await contexto.SolicitudesExportacionCompleta
            // Una en curso sin terminar o una sin reclamar durante el umbral: las dos se dan por fallidas para que no
            // guarden la contraseña indefinidamente ni ocupen el índice de solicitud viva del Tenant.
            .Where(s => (s.Estado == EstadoSolicitudExportacionCompleta.EnCurso && s.IniciadaEnUtc < limite)
                || (s.Estado == EstadoSolicitudExportacionCompleta.Pendiente && s.SolicitadaEnUtc < limite))
            .ToListAsync(cancellationToken);
        if (estancadas.Count == 0)
            return;

        foreach (var solicitud in estancadas)
        {
            solicitud.MarcarFallida("Estancada", ahora);
            contexto.RegistrosAuditoria.Add(AuditoriaExportacionCompleta.DelSistema(
                solicitud.Id, AuditoriaExportacionCompleta.FaseFallida, new { motivo = "Estancada" }));
        }
        await contexto.SaveChangesAsync(cancellationToken);
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
            // Primero el fichero: si el borrado falla, la fila sigue Lista y la próxima vuelta lo reintenta; la
            // descarga ya la impide la fecha de caducidad.
            if (solicitud.UbicacionFichero is { } ubicacion)
                almacen.Eliminar(tenantId, ubicacion);

            solicitud.MarcarCaducada(ahora);
            contexto.RegistrosAuditoria.Add(AuditoriaExportacionCompleta.DelSistema(
                solicitud.Id, AuditoriaExportacionCompleta.FaseCaducada));
            await contexto.SaveChangesAsync(cancellationToken);
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
        try
        {
            using var ambito = ambitoFactory.CreateScope();
            var proveedor = ambito.ServiceProvider;
            var contexto = proveedor.GetRequiredService<CaeManagerDbContext>();
            var almacen = proveedor.GetRequiredService<AlmacenExportacionesCompletasEnDisco>();
            var documentos = proveedor.GetRequiredService<IFileStorageService>();
            var protector = proveedor.GetRequiredService<IProtectorContrasenaExportacion>();

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

            contexto.ChangeTracker.Clear();
            var lista = await contexto.SolicitudesExportacionCompleta.SingleAsync(s => s.Id == id, cancellationToken);
            lista.MarcarLista(ubicacion, tamano, Ahora());
            contexto.RegistrosAuditoria.Add(AuditoriaExportacionCompleta.DelSistema(
                id, AuditoriaExportacionCompleta.FaseLista,
                new { tamanoBytes = tamano, recuentos = resultado!.Recuentos, documentosSinFichero = resultado.DocumentosSinFichero }));
            await contexto.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            var motivo = ex is OperationCanceledException && cancellationToken.IsCancellationRequested ? "Interrumpida" : "GeneracionFallida";
            logger.LogError(ex, "Falló la exportación completa {SolicitudId} del Tenant {TenantId}.", id, tenantId);
            await FallarAsync(tenantId, id, motivo, ubicacionConfirmada);
            if (motivo == "Interrumpida")
                throw;
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

        if (ubicacionConfirmada is not null)
            ambito.ServiceProvider.GetRequiredService<AlmacenExportacionesCompletasEnDisco>().Eliminar(tenantId, ubicacionConfirmada);

        solicitud.MarcarFallida(motivo, Ahora());
        contexto.RegistrosAuditoria.Add(AuditoriaExportacionCompleta.DelSistema(
            id, AuditoriaExportacionCompleta.FaseFallida, new { motivo }));
        await contexto.SaveChangesAsync(CancellationToken.None);
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
