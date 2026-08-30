using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Configuracion;
using CaeManager.Infrastructure.Coordinacion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Consume la cola de <see cref="EventoWebhook"/> por sondeo corto — mismo
/// patrón que <c>ProcesadorAnalisisDocumentoHostedService</c> (P2 #22).
/// Un tenant a la vez, dentro de <see cref="AmbitoTenantExplicito"/>, y
/// envuelto en elección de líder (P3-30) para que solo una réplica procese
/// cada tick.
/// </summary>
public class IngestaWebhookHostedService(
    IServiceScopeFactory ambitoFactory, IEleccionLiderService eleccionLider, ILogger<IngestaWebhookHostedService> logger)
    : BackgroundService
{
    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromSeconds(10);

    /// <summary>Mismo umbral que <c>ProcesadorAnalisisDocumentoHostedService.UmbralEstancado</c> — la ingesta de Graph es más rápida que un análisis IA, pero un proceso caído a mitad de ingesta deja el evento igual de atascado.</summary>
    private static readonly TimeSpan UmbralEstancado = TimeSpan.FromMinutes(15);

    /// <summary>Máximo de eventos que se procesan de un mismo tenant antes de pasar al siguiente dentro del mismo tick — ver el comentario del mismo nombre en <c>ProcesadorAnalisisDocumentoHostedService</c>.</summary>
    private const int LoteMaximoPorTenant = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var temporizador = new PeriodicTimer(IntervaloSondeo);
        do
        {
            try
            {
                await eleccionLider.IntentarEjecutarComoLiderAsync("ingesta-webhook-microsoft365", SondearTodosLosTenantsAsync, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Un fallo en el propio sondeo no puede tumbar el BackgroundService entero — el siguiente tick lo reintenta.
                logger.LogError(ex, "Falló un ciclo de sondeo de la cola de ingesta de webhooks.");
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

        foreach (var tenantId in tenantsActivos)
        {
            stoppingToken.ThrowIfCancellationRequested();

            // Aislado igual que en ProcesadorAnalisisDocumentoHostedService:
            // sin este try/catch, una excepción en el tenant k (p. ej. Graph
            // devolviendo un error persistente para su conexión) abortaba
            // este foreach entero y dejaba sin procesar a k+1..N — como el
            // orden de tenantsActivos es estable, el mismo tenant volvía a
            // fallar en el mismo punto en cada tick y bloqueaba indefinidamente
            // a los mismos siguientes.
            try
            {
                await ProcesarPendientesDelTenantAsync(tenantId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Falló el sondeo de la cola de ingesta de webhooks del tenant {TenantId}; se continúa con el resto de tenants en este tick.",
                    tenantId);
            }
        }
    }

    private async Task ProcesarPendientesDelTenantAsync(Guid tenantId, CancellationToken stoppingToken)
    {
        // El interruptor se comprueba una vez por tick de sondeo (no por
        // evento): apagarlo dentro de un lote en curso no interrumpe los
        // eventos ya cogidos, solo evita que se coja el siguiente lote.
        using (var ambitoComprobacion = ambitoFactory.CreateScope())
        {
            using var _ = AmbitoTenantExplicito.Establecer(tenantId);
            var registroComprobacion = ambitoComprobacion.ServiceProvider.GetRequiredService<IRegistroAutomatizacionesService>();
            if (!await registroComprobacion.EstaActivoAsync(CatalogoAutomatizaciones.IngestaCorreoM365, stoppingToken))
                return;
        }

        await RecuperarEstancadosAsync(tenantId, stoppingToken);

        var huboActividad = false;
        var procesadosEnEsteTick = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested && procesadosEnEsteTick < LoteMaximoPorTenant)
            {
                using var ambito = ambitoFactory.CreateScope();
                using var _ = AmbitoTenantExplicito.Establecer(tenantId);

                var eventoRepositorio = ambito.ServiceProvider.GetRequiredService<IEventoWebhookRepository>();

                // Reclamo atómico (FOR UPDATE SKIP LOCKED + "Procesando" en la
                // misma transacción) — ver el comentario de
                // IEventoWebhookRepository.ReclamarSiguientePendienteAsync.
                var evento = await eventoRepositorio.ReclamarSiguientePendienteAsync(ProveedorIntegracion.Microsoft365, stoppingToken);
                if (evento is null) break;

                procesadosEnEsteTick++;
                huboActividad = true;

                var ingesta = ambito.ServiceProvider.GetRequiredService<IngestaWebhookService>();

                try
                {
                    await ingesta.ProcesarAsync(evento, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Apagado normal: se devuelve a Pendiente de inmediato en
                    // vez de dejarlo en "Procesando" hasta que
                    // RecuperarEstancadosAsync lo note en el próximo arranque
                    // — CancellationToken.None porque esto debe guardarse
                    // aunque la cancelación ya esté pedida.
                    evento.DevolverAPendienteTrasCancelacion();
                    await ambito.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
                    throw;
                }

                await ambito.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await RegistrarEjecucionAsync(tenantId, exitosa: false, stoppingToken);
            throw;
        }

        // Se registra "última ejecución" solo cuando hubo algo que
        // procesar — el sondeo corre cada 10s indefinidamente, y marcar
        // "ejecutado" en cada tick vacío no aportaría nada a la pantalla
        // de Automatizaciones, solo ruido en la fecha.
        if (huboActividad)
            await RegistrarEjecucionAsync(tenantId, exitosa: true, stoppingToken);
    }

    private async Task RecuperarEstancadosAsync(Guid tenantId, CancellationToken stoppingToken)
    {
        using var ambito = ambitoFactory.CreateScope();
        using var _ = AmbitoTenantExplicito.Establecer(tenantId);

        var repositorio = ambito.ServiceProvider.GetRequiredService<IEventoWebhookRepository>();
        var estancados = await repositorio.ObtenerEstancadosAsync(ProveedorIntegracion.Microsoft365, UmbralEstancado, stoppingToken);
        if (estancados.Count == 0) return;

        var ahora = DateTime.UtcNow;
        foreach (var evento in estancados)
        {
            logger.LogWarning(
                "Evento de webhook {EventoId} (tenant {TenantId}) llevaba más de {Umbral} en \"Procesando\" — se recupera.",
                evento.Id, tenantId, UmbralEstancado);
            evento.RecuperarSiEstancado(UmbralEstancado, ahora);
        }

        await ambito.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(stoppingToken);
    }

    private async Task RegistrarEjecucionAsync(Guid tenantId, bool exitosa, CancellationToken stoppingToken)
    {
        using var ambito = ambitoFactory.CreateScope();
        using var _ = AmbitoTenantExplicito.Establecer(tenantId);
        var registro = ambito.ServiceProvider.GetRequiredService<IRegistroAutomatizacionesService>();
        await registro.RegistrarEjecucionAsync(CatalogoAutomatizaciones.IngestaCorreoM365, exitosa, stoppingToken);
    }
}
