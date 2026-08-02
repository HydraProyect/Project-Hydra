using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
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
            await ProcesarPendientesDelTenantAsync(tenantId, stoppingToken);
        }
    }

    private async Task ProcesarPendientesDelTenantAsync(Guid tenantId, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var ambito = ambitoFactory.CreateScope();
            using var _ = AmbitoTenantExplicito.Establecer(tenantId);

            var eventoRepositorio = ambito.ServiceProvider.GetRequiredService<IEventoWebhookRepository>();
            var evento = await eventoRepositorio.ObtenerSiguientePendienteAsync(stoppingToken);
            if (evento is null) return;

            var ingesta = ambito.ServiceProvider.GetRequiredService<IngestaWebhookService>();
            await ingesta.ProcesarAsync(evento, stoppingToken);

            await ambito.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(stoppingToken);
        }
    }
}
