using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Coordinacion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Redacta el payload crudo de <see cref="EventoWebhook"/> ya resuelto
/// (Completado/DescartadoDefinitivo) pasado <see cref="RetencionEventosWebhookOptions.DiasRetencion"/>
/// (auditoría módulo 6). Sondeo diario de sobra: la retención se cuenta en
/// días, no en minutos. Envuelto en elección de líder e iterado por tenant,
/// mismo patrón que <see cref="RenovacionSuscripcionWebhookHostedService"/>.
/// </summary>
public class RedaccionPayloadWebhookHostedService(
    IServiceScopeFactory ambitoFactory, IEleccionLiderService eleccionLider,
    IOptions<RetencionEventosWebhookOptions> opciones, ILogger<RedaccionPayloadWebhookHostedService> logger)
    : BackgroundService
{
    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromHours(24);

    /// <summary>Tamaño de lote por tenant y vuelta — acota la memoria de un backlog grande sin dejar de avanzar hasta agotarlo.</summary>
    private const int TamanoLote = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!opciones.Value.Activa)
        {
            logger.LogInformation("Redacción de payload de webhook desactivada (RetencionEventosWebhook:Activa=false) — no se ejecuta.");
            return;
        }

        using var temporizador = new PeriodicTimer(IntervaloSondeo);

        await IntentarRedactarTodosLosTenantsAsync(stoppingToken);

        while (await temporizador.WaitForNextTickAsync(stoppingToken))
            await IntentarRedactarTodosLosTenantsAsync(stoppingToken);
    }

    private async Task IntentarRedactarTodosLosTenantsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await eleccionLider.IntentarEjecutarComoLiderAsync("redaccion-payload-webhook", RedactarTodosLosTenantsAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló un ciclo de redacción de payload de webhook.");
        }
    }

    private async Task RedactarTodosLosTenantsAsync(CancellationToken stoppingToken)
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

            // Aislado igual que RenovacionSuscripcionWebhookHostedService:
            // un tenant con datos inesperados no debe dejar sin redactar a
            // los siguientes.
            try
            {
                await RedactarPendientesDelTenantAsync(tenantId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Falló la redacción de payload de webhook del tenant {TenantId}; se continúa con el resto de tenants en este ciclo.",
                    tenantId);
            }
        }
    }

    private async Task RedactarPendientesDelTenantAsync(Guid tenantId, CancellationToken stoppingToken)
    {
        var limiteUtc = DateTime.UtcNow.AddDays(-opciones.Value.DiasRetencion);

        // Bucle de lotes: sigue mientras el tenant tenga candidatos, con un
        // ámbito/DbContext nuevo por lote para no acumular tracking sin
        // límite en un backlog grande.
        while (true)
        {
            stoppingToken.ThrowIfCancellationRequested();

            using var ambito = ambitoFactory.CreateScope();
            using var _ = AmbitoTenantExplicito.Establecer(tenantId);

            var repositorio = ambito.ServiceProvider.GetRequiredService<IEventoWebhookRepository>();
            var candidatos = await repositorio.ObtenerParaRedactarAsync(limiteUtc, TamanoLote, stoppingToken);
            if (candidatos.Count == 0) return;

            foreach (var evento in candidatos)
                evento.RedactarPayload();

            await ambito.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(stoppingToken);

            // Un lote incompleto significa que ya no quedan más candidatos.
            if (candidatos.Count < TamanoLote) return;
        }
    }
}
