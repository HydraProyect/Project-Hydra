using CaeManager.Application.Common;
using CaeManager.Application.Documentos.ValidacionOficial;
using CaeManager.Application.Documentos.Verificacion;
using CaeManager.Application.DocumentosIa;
using CaeManager.Application.Tenants;
using CaeManager.Application.Trabajadores.Deteccion;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Coordinacion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.DocumentosIa;

/// <summary>
/// Consume la cola durable de <see cref="ITrabajoAnalisisDocumentoRepository"/>
/// (P2 #22 de docs/business/MATURITY_REVIEW.md) y ejecuta los análisis fuera
/// del circuito de Blazor, avisando al usuario por la campana
/// (<see cref="NotificacionUsuario"/>) cuando terminan.
///
/// Por sondeo, no por notificación push: no hay backplane entre réplicas
/// (ver DEPLOY.md, "una sola réplica") así que un mecanismo de "avísame
/// cuando llegue trabajo" no tendría con quién comunicarse igual — el
/// sondeo es la opción simple que además sobrevive sola a un reinicio del
/// proceso, que es justo el problema que la cola en memoria anterior tenía.
///
/// Un tenant a la vez, nunca una consulta que cruce tenants: el filtro
/// global no se puede saltar sin <c>IgnoreQueryFilters()</c>, y este
/// servicio no lo necesita — <c>Tenants</c> es catálogo global (sin
/// TenantId), así que listar los tenants activos no cruza nada, y cada
/// trabajo pendiente se pide ya dentro del ámbito de un tenant concreto
/// (<see cref="AmbitoTenantExplicito"/>, docs/MULTITENANCY.md § 8.4) — mismo
/// patrón que <c>ObtenerKpisGlobalesQuery</c>.
///
/// Desde Horizonte 2.4, cada sondeo también vigila si la cola está
/// "estancada" (ver <see cref="UmbralColaEstancada"/>) y avisa por
/// <see cref="IAlertaOperativa"/> — la alerta de guardia, distinta de
/// <see cref="RecuperarEstancadosAsync"/>, que recupera trabajos
/// individuales colgados en "Procesando" y no avisa a nadie porque se
/// autocorrige solo.
/// </summary>
public class ProcesadorAnalisisDocumentoHostedService(
    IServiceScopeFactory ambitoFactory,
    IEleccionLiderService eleccionLider,
    IAlertaOperativa alertaOperativa,
    ILogger<ProcesadorAnalisisDocumentoHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Cuánto puede llevar un trabajo en "Procesando" antes de asumir que el
    /// proceso que lo reclamó se cayó o se redesplegó a mitad de análisis.
    /// Generoso a propósito: los análisis IA tardan segundos, no minutos, así
    /// que 15 minutos no compite nunca con uno que sigue en curso de verdad.
    /// </summary>
    private static readonly TimeSpan UmbralEstancado = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Umbral de "cola de IA estancada" (Horizonte 2.4): cuánto puede llevar
    /// el trabajo Pendiente más antiguo esperando antes de avisar. Mayor que
    /// <see cref="UmbralEstancado"/> a propósito — ese umbral ya recupera
    /// trabajos individuales colgados en "Procesando"; este otro detecta el
    /// síntoma más grave de que el motor entero no avanza (p. ej. el
    /// proveedor de IA está caído y cada intento falla y se reencola), no un
    /// trabajo suelto. Edad del más antiguo, no "N sondeos seguidos por
    /// encima de un umbral de profundidad": es más simple de calcular
    /// correctamente (una consulta ya existente,
    /// <see cref="ITrabajoAnalisisDocumentoRepository.ObtenerSiguientePendienteAsync"/>,
    /// sin contador propio que perder si el proceso se reinicia) y mide
    /// exactamente lo que le importa al operador de guardia: cuánto lleva
    /// esperando el documento que más lleva esperando.
    /// </summary>
    private static readonly TimeSpan UmbralColaEstancada = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Sin esto, cada sondeo (cada 5 s) mientras la cola sigue estancada
    /// generaría un evento de Sentry nuevo — mismo problema en miniatura que
    /// el propio dead man's switch busca evitar en el sentido contrario:
    /// ruido en vez de silencio. Un aviso cada media hora mientras la
    /// condición persiste es suficiente para que la guardia se entere sin
    /// ahogar el canal.
    /// </summary>
    private static readonly TimeSpan CooldownAlertaColaEstancada = TimeSpan.FromMinutes(30);

    private DateTime? _ultimaAlertaColaEstancadaUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var temporizador = new PeriodicTimer(IntervaloSondeo);
        do
        {
            try
            {
                // Elección de líder entre réplicas (P3-30 de docs/business/MATURITY_REVIEW.md):
                // sin bloqueo de fila en ObtenerSiguientePendienteAsync, dos
                // réplicas sondeando a la vez podrían tomar el mismo
                // TrabajoAnalisisDocumento y analizarlo dos veces. Solo la
                // que gana el advisory lock sondea este tick; las demás lo
                // saltan y lo vuelven a intentar en el siguiente.
                await eleccionLider.IntentarEjecutarComoLiderAsync(
                    "procesador-analisis-documento", SondearTodosLosTenantsAsync, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Un fallo en el propio sondeo (p. ej. la base de datos
                // momentáneamente inalcanzable) no puede tumbar el
                // BackgroundService entero — el siguiente tick lo reintenta.
                logger.LogError(ex, "Falló un ciclo de sondeo de la cola de análisis IA.");
            }
        }
        while (await temporizador.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SondearTodosLosTenantsAsync(CancellationToken stoppingToken)
    {
        List<Guid> tenantsActivos;
        using (var ambito = ambitoFactory.CreateScope())
        {
            tenantsActivos = await ambito.ServiceProvider.GetRequiredService<ITenantsQueryContext>()
                .Tenants.Where(t => t.Estado == EstadoTenant.Activo)
                .Select(t => t.Id)
                .ToListAsync(stoppingToken);
        }

        // Se mide antes de procesar, no después: "profundidad de cola de IA"
        // (Horizonte 2.3) es el atasco real en este instante, y
        // ProcesarPendientesDelTenantAsync vacía la cola de cada tenant hasta
        // dejarla en 0 — medir al final siempre daría un gauge en cero.
        await MedirProfundidadColaAsync(tenantsActivos, stoppingToken);

        foreach (var tenantId in tenantsActivos)
        {
            stoppingToken.ThrowIfCancellationRequested();
            await ProcesarPendientesDelTenantAsync(tenantId, stoppingToken);
        }
    }

    private async Task MedirProfundidadColaAsync(IReadOnlyList<Guid> tenantsActivos, CancellationToken stoppingToken)
    {
        var total = 0;
        TrabajoAnalisisDocumento? masAntiguoPendiente = null;

        foreach (var tenantId in tenantsActivos)
        {
            using var ambito = ambitoFactory.CreateScope();
            using var _ = AmbitoTenantExplicito.Establecer(tenantId);

            var repositorio = ambito.ServiceProvider.GetRequiredService<ITrabajoAnalisisDocumentoRepository>();
            total += await repositorio.ContarActivosAsync(stoppingToken);

            // Reutiliza la misma consulta que ya usa el sondeo real (ordena
            // por CreadoEnUtc ascendente) solo para leer su antigüedad — no
            // hace falta un método de repositorio nuevo para esto.
            var pendienteDelTenant = await repositorio.ObtenerSiguientePendienteAsync(stoppingToken);
            if (pendienteDelTenant is not null &&
                (masAntiguoPendiente is null || pendienteDelTenant.CreadoEnUtc < masAntiguoPendiente.CreadoEnUtc))
            {
                masAntiguoPendiente = pendienteDelTenant;
            }
        }

        Observabilidad.ActualizarColaIaProfundidad(total);
        AvisarSiColaEstancada(masAntiguoPendiente);
    }

    /// <summary>Ver <see cref="UmbralColaEstancada"/> y <see cref="CooldownAlertaColaEstancada"/> para el razonamiento de los dos umbrales.</summary>
    private void AvisarSiColaEstancada(TrabajoAnalisisDocumento? masAntiguoPendiente)
    {
        if (masAntiguoPendiente is null) return;

        var ahora = DateTime.UtcNow;
        var antiguedad = ahora - masAntiguoPendiente.CreadoEnUtc;
        if (antiguedad < UmbralColaEstancada) return;

        if (_ultimaAlertaColaEstancadaUtc is { } ultima && ahora - ultima < CooldownAlertaColaEstancada) return;
        _ultimaAlertaColaEstancadaUtc = ahora;

        alertaOperativa.Emitir(
            $"Cola de análisis IA estancada: el trabajo pendiente más antiguo (documento {masAntiguoPendiente.DocumentoId}) lleva {antiguedad.TotalMinutes:F0} min esperando (umbral {UmbralColaEstancada.TotalMinutes:F0} min).",
            NivelAlertaOperativa.Critica);
    }

    private async Task ProcesarPendientesDelTenantAsync(Guid tenantId, CancellationToken stoppingToken)
    {
        await RecuperarEstancadosAsync(tenantId, stoppingToken);

        // Aísla en Sentry el historial de reintentos de cada trabajo (D3,
        // decisión del propietario del producto): ver SeguimientoReintentosAnalisisIa
        // (Application) para el porqué y el aislamiento por ámbito.
        using var seguimiento = new SeguimientoReintentosAnalisisIa(alertaOperativa);

        while (!stoppingToken.IsCancellationRequested)
        {
            using var ambito = ambitoFactory.CreateScope();
            using var _ = AmbitoTenantExplicito.Establecer(tenantId);

            var repositorio = ambito.ServiceProvider.GetRequiredService<ITrabajoAnalisisDocumentoRepository>();
            var trabajo = await repositorio.ObtenerSiguientePendienteAsync(stoppingToken);
            if (trabajo is null) return;

            seguimiento.AlEmpezarIntento(trabajo.Id);

            // Se marca "Procesando" y se guarda antes de ejecutar el análisis
            // — si el proceso se cae durante el análisis, el trabajo queda
            // visible como estancado (RecuperarEstancadosAsync lo retoma en
            // el próximo sondeo) en vez de perderse.
            trabajo.MarcarEnProceso();
            await ambito.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(stoppingToken);

            try
            {
                await EjecutarAnalisisAsync(ambito.ServiceProvider, trabajo, stoppingToken);
                trabajo.MarcarCompletado();
                // "Documentos procesados/hora" del plan (Horizonte 2.3): un
                // documento cuenta aquí, no al subirlo — es el momento en que
                // el análisis IA terminó de verdad, con éxito.
                Observabilidad.DocumentosProcesados.Add(1, new KeyValuePair<string, object?>("Tipo", trabajo.Tipo.ToString()));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Apagado normal de la aplicación: se deja "Procesando" tal
                // cual, sin contar como fallo — RecuperarEstancadosAsync lo
                // retoma pasado el umbral en el próximo arranque.
                throw;
            }
            catch (Exception ex)
            {
                // Mismo criterio de "mejor esfuerzo" que tenía la cola en
                // memoria: el análisis puede fallar sin que eso invalide el
                // documento ya subido. Lo que cambia es que ahora reintenta
                // hasta TrabajoAnalisisDocumento.MaximoIntentos veces antes
                // de darlo por perdido, en vez de una sola línea de log.
                logger.LogError(ex,
                    "Falló el análisis {Tipo} del trabajo {TrabajoId} (documento {DocumentoId}, tenant {TenantId}, intento {Intento}).",
                    trabajo.Tipo, trabajo.Id, trabajo.DocumentoId, tenantId, trabajo.Intentos + 1);

                seguimiento.RegistrarFallo(trabajo, ex);
            }

            await AvisarSiCorrespondeAsync(ambito.ServiceProvider, trabajo, stoppingToken);
            await ambito.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(stoppingToken);
        }
    }

    private async Task RecuperarEstancadosAsync(Guid tenantId, CancellationToken stoppingToken)
    {
        using var ambito = ambitoFactory.CreateScope();
        using var _ = AmbitoTenantExplicito.Establecer(tenantId);

        var repositorio = ambito.ServiceProvider.GetRequiredService<ITrabajoAnalisisDocumentoRepository>();
        var estancados = await repositorio.ObtenerEstancadosAsync(UmbralEstancado, stoppingToken);
        if (estancados.Count == 0) return;

        var ahora = DateTime.UtcNow;
        foreach (var trabajo in estancados)
        {
            logger.LogWarning(
                "Trabajo {TrabajoId} (documento {DocumentoId}, tenant {TenantId}) llevaba más de {Umbral} en \"Procesando\" — se recupera.",
                trabajo.Id, trabajo.DocumentoId, tenantId, UmbralEstancado);
            trabajo.RecuperarSiEstancado(UmbralEstancado, ahora);
        }

        await ambito.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(stoppingToken);
    }

    private static async Task EjecutarAnalisisAsync(
        IServiceProvider servicios, TrabajoAnalisisDocumento trabajo, CancellationToken cancellationToken)
    {
        switch (trabajo.Tipo)
        {
            case TipoAnalisisDocumento.VerificacionIa:
                await servicios.GetRequiredService<IVerificacionIaDocumentoService>()
                    .ProcesarDocumentoAsync(trabajo.DocumentoId, cancellationToken);
                break;

            case TipoAnalisisDocumento.DeteccionTrabajadores:
                await servicios.GetRequiredService<IDeteccionTrabajadoresService>()
                    .ProcesarDocumentoAsync(trabajo.DocumentoId, cancellationToken);
                break;

            case TipoAnalisisDocumento.VerificacionFirmaDigital:
                await servicios.GetRequiredService<IValidacionDocumentoOficialService>()
                    .ProcesarDocumentoAsync(trabajo.DocumentoId, cancellationToken);
                break;
        }
    }

    /// <summary>
    /// La campana, no un correo: el usuario que acaba de subir el documento
    /// suele seguir en la aplicación, y <see cref="NotificacionUsuario"/>
    /// sobrevive a recargas y a cerrar sesión, así que tampoco se pierde si
    /// no lo está.
    ///
    /// Dos desenlaces avisan, y con mensajes distintos a propósito (D3): al
    /// completar con éxito, que ya está revisado; al agotar
    /// <see cref="TrabajoAnalisisDocumento.MaximoIntentos"/> (o al fallar de
    /// forma definitiva) sin conseguirlo, que la verificación no está
    /// disponible — nunca el primer mensaje para el segundo caso, que es
    /// justo la mentira que este aviso tenía antes. El texto del segundo
    /// caso (decisión del propietario del producto) separa a propósito dos
    /// hechos que el usuario podría confundir: el Documento SÍ se guardó —
    /// eso no falló — lo que no está disponible es la verificación
    /// automática. Dejarlo ambiguo empuja al usuario a volver a subir el
    /// documento por si acaso, creyendo que se perdió. Mientras quedan
    /// reintentos (Estado vuelve a Pendiente) no se avisa nada todavía: es
    /// un fallo transitorio que el propio sondeo va a reintentar en
    /// segundos, no algo que el usuario tenga que atender ya.
    /// </summary>
    private static Task AvisarSiCorrespondeAsync(
        IServiceProvider servicios, TrabajoAnalisisDocumento trabajo, CancellationToken cancellationToken)
    {
        if (trabajo.UsuarioSolicitanteId is not { } usuarioId) return Task.CompletedTask;

        var aviso = trabajo.Estado switch
        {
            EstadoTrabajoAnalisisDocumento.Completado => ObtenerAvisoCompletado(trabajo.Tipo),
            EstadoTrabajoAnalisisDocumento.Fallido => ObtenerAvisoFallido(trabajo.Tipo),
            _ => default((string Titulo, string Mensaje, string UrlAccion, string TextoAccion)?),
        };

        if (aviso is not { } valores) return Task.CompletedTask;

        servicios.GetRequiredService<INotificacionUsuarioRepository>()
            .Agregar(new NotificacionUsuario(usuarioId, valores.Titulo, valores.Mensaje, valores.UrlAccion, valores.TextoAccion));

        return Task.CompletedTask;
    }

    // Con tres tipos, los ternarios originales ya no escalaban.
    private static (string Titulo, string Mensaje, string UrlAccion, string TextoAccion) ObtenerAvisoCompletado(TipoAnalisisDocumento tipo) => tipo switch
    {
        TipoAnalisisDocumento.VerificacionIa => (
            "Verificación automática terminada",
            "Ya está revisado el documento que subiste. Comprueba el resultado por si necesita tu confirmación.",
            "/documentos/revision-ia", "Ver revisión"),
        TipoAnalisisDocumento.DeteccionTrabajadores => (
            "Detección de personal terminada",
            "Ya se ha analizado el documento que subiste en busca de altas y bajas de personal.",
            "/trabajadores", "Ver trabajadores"),
        _ => (
            "Validación de documento oficial terminada",
            "Ya se ha verificado la firma digital del documento que subiste y cotejado sus datos.",
            "/documentos", "Ver documentos"),
    };

    private static (string Titulo, string Mensaje, string UrlAccion, string TextoAccion) ObtenerAvisoFallido(TipoAnalisisDocumento tipo) => tipo switch
    {
        TipoAnalisisDocumento.VerificacionIa => (
            "Verificación automática no disponible",
            "El documento se guardó correctamente. No hemos podido revisarlo por IA — compruébalo tú manualmente.",
            "/documentos", "Ver documentos"),
        TipoAnalisisDocumento.DeteccionTrabajadores => (
            "Detección de personal no disponible",
            "El documento se guardó correctamente. No hemos podido analizarlo automáticamente en busca de altas y bajas de personal — revísalo tú manualmente.",
            "/trabajadores", "Ver trabajadores"),
        _ => (
            "Validación de documento oficial no disponible",
            "El documento se guardó correctamente. No hemos podido verificar automáticamente su firma digital — revísalo tú manualmente.",
            "/documentos", "Ver documentos"),
    };
}
