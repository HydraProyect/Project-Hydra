using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Verificacion;
using CaeManager.Application.Trabajadores.Deteccion;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Tenants;
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
/// </summary>
public class ProcesadorAnalisisDocumentoHostedService(
    IServiceScopeFactory ambitoFactory,
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var temporizador = new PeriodicTimer(IntervaloSondeo);
        do
        {
            try
            {
                await SondearTodosLosTenantsAsync(stoppingToken);
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
            tenantsActivos = await ambito.ServiceProvider.GetRequiredService<IApplicationDbContext>()
                .Tenants.Where(t => t.Estado == EstadoTenant.Activo)
                .Select(t => t.Id)
                .ToListAsync(stoppingToken);
        }

        foreach (var tenantId in tenantsActivos)
        {
            stoppingToken.ThrowIfCancellationRequested();
            await ProcesarPendientesDelTenantAsync(tenantId, stoppingToken);
        }
    }

    private async Task ProcesarPendientesDelTenantAsync(Guid tenantId, CancellationToken stoppingToken)
    {
        await RecuperarEstancadosAsync(tenantId, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            using var ambito = ambitoFactory.CreateScope();
            using var _ = AmbitoTenantExplicito.Establecer(tenantId);

            var repositorio = ambito.ServiceProvider.GetRequiredService<ITrabajoAnalisisDocumentoRepository>();
            var trabajo = await repositorio.ObtenerSiguientePendienteAsync(stoppingToken);
            if (trabajo is null) return;

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
                trabajo.RegistrarFallo(ex.Message);
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
        }
    }

    /// <summary>
    /// La campana, no un correo: el usuario que acaba de subir el documento
    /// suele seguir en la aplicación, y <see cref="NotificacionUsuario"/>
    /// sobrevive a recargas y a cerrar sesión, así que tampoco se pierde si
    /// no lo está. Solo al completar con éxito — igual que antes, un fallo
    /// nunca notificaba (ver criterio de mejor esfuerzo más arriba).
    /// </summary>
    private static Task AvisarSiCorrespondeAsync(
        IServiceProvider servicios, TrabajoAnalisisDocumento trabajo, CancellationToken cancellationToken)
    {
        if (trabajo.Estado != EstadoTrabajoAnalisisDocumento.Completado) return Task.CompletedTask;
        if (trabajo.UsuarioSolicitanteId is not { } usuarioId) return Task.CompletedTask;

        var titulo = trabajo.Tipo == TipoAnalisisDocumento.VerificacionIa
            ? "Verificación automática terminada"
            : "Detección de personal terminada";

        var mensaje = trabajo.Tipo == TipoAnalisisDocumento.VerificacionIa
            ? "Ya está revisado el documento que subiste. Comprueba el resultado por si necesita tu confirmación."
            : "Ya se ha analizado el documento que subiste en busca de altas y bajas de personal.";

        var urlAccion = trabajo.Tipo == TipoAnalisisDocumento.VerificacionIa ? "/documentos/revision-ia" : "/trabajadores";
        var textoAccion = trabajo.Tipo == TipoAnalisisDocumento.VerificacionIa ? "Ver revisión" : "Ver trabajadores";

        servicios.GetRequiredService<INotificacionUsuarioRepository>()
            .Agregar(new NotificacionUsuario(usuarioId, titulo, mensaje, urlAccion, textoAccion));

        return Task.CompletedTask;
    }
}
