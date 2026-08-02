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
/// Renueva las <see cref="SuscripcionWebhook"/> próximas a expirar (Graph
/// solo admite ~2.94 días por suscripción de correo, ver
/// <c>Microsoft365GraphClient</c>) — un sondeo diario es de sobra frente a
/// esa ventana. Envuelto en elección de líder (P3-30), igual que
/// <see cref="IngestaWebhookHostedService"/>.
/// </summary>
public class RenovacionSuscripcionWebhookHostedService(
    IServiceScopeFactory ambitoFactory, IEleccionLiderService eleccionLider, ILogger<RenovacionSuscripcionWebhookHostedService> logger)
    : BackgroundService
{
    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromHours(24);
    private static readonly TimeSpan VentanaRenovacion = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var temporizador = new PeriodicTimer(IntervaloSondeo);

        await IntentarRenovarTodosLosTenantsAsync(stoppingToken);

        while (await temporizador.WaitForNextTickAsync(stoppingToken))
            await IntentarRenovarTodosLosTenantsAsync(stoppingToken);
    }

    private async Task IntentarRenovarTodosLosTenantsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await eleccionLider.IntentarEjecutarComoLiderAsync("renovacion-suscripcion-webhook-microsoft365", RenovarTodosLosTenantsAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló un ciclo de renovación de suscripciones de webhook.");
        }
    }

    private async Task RenovarTodosLosTenantsAsync(CancellationToken stoppingToken)
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
            await RenovarPendientesDelTenantAsync(tenantId, stoppingToken);
        }
    }

    private async Task RenovarPendientesDelTenantAsync(Guid tenantId, CancellationToken stoppingToken)
    {
        using var ambito = ambitoFactory.CreateScope();
        using var _ = AmbitoTenantExplicito.Establecer(tenantId);

        var suscripcionRepositorio = ambito.ServiceProvider.GetRequiredService<ISuscripcionWebhookRepository>();
        var proximasAExpirar = await suscripcionRepositorio.ObtenerProximasAExpirarAsync(VentanaRenovacion, stoppingToken);
        if (proximasAExpirar.Count == 0) return;

        var accesoGraph = ambito.ServiceProvider.GetRequiredService<AccesoGraphService>();
        var graphClient = ambito.ServiceProvider.GetRequiredService<IMicrosoft365GraphClient>();

        foreach (var suscripcion in proximasAExpirar)
        {
            var accessTokenResultado = await accesoGraph.ObtenerAccessTokenVigenteAsync(suscripcion.ConexionIntegracionId, stoppingToken);
            if (accessTokenResultado.EsFallido)
            {
                logger.LogWarning(
                    "No se pudo renovar la suscripción {SubscriptionId} (conexión {ConexionId}): {Error}",
                    suscripcion.GraphSubscriptionId, suscripcion.ConexionIntegracionId, accessTokenResultado.Error.Mensaje);
                continue;
            }

            var renovacionResultado = await graphClient.RenovarSuscripcionAsync(
                accessTokenResultado.Valor, suscripcion.GraphSubscriptionId, stoppingToken);
            if (renovacionResultado.EsFallido)
            {
                logger.LogWarning(
                    "No se pudo renovar la suscripción {SubscriptionId} (conexión {ConexionId}): {Error}",
                    suscripcion.GraphSubscriptionId, suscripcion.ConexionIntegracionId, renovacionResultado.Error.Mensaje);
                continue;
            }

            suscripcion.ActualizarTrasRenovacion(renovacionResultado.Valor.GraphSubscriptionId, renovacionResultado.Valor.FechaExpiracionUtc);
        }

        await ambito.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(stoppingToken);
    }
}
