using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Coordinacion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Reintento acotado de la verificación de identidad de los buzones de Microsoft 365. El ciclo diario de
/// <see cref="RenovacionSuscripcionWebhookHostedService"/> verifica cada 24 h con una ventana de 26 h: un solo fallo
/// transitorio dejaba la conexión bloqueada hasta el ciclo siguiente. Este servicio atiende solo a las conexiones
/// Habilitadas sin verificar o con la última verificación de hace más de
/// <see cref="Domain.Integraciones.ConexionIntegracion.UmbralReintentoVerificacion"/>, y cada una se reintenta como
/// mucho cada <see cref="Domain.Integraciones.ConexionIntegracion.IntervaloReintentoVerificacion"/> más un margen
/// aleatorio (o más tarde, si Microsoft pidió esperar con Retry-After). La programación vive en la propia conexión y la
/// exclusión por conexión en <see cref="ICerrojoVerificacionBuzon"/>: ver
/// <see cref="CompletarIdentidadBuzonService.ReintentarAsync"/>.
/// </summary>
public class ReintentoVerificacionIdentidadBuzonHostedService(
    IServiceScopeFactory ambitoFactory, IEleccionLiderService eleccionLider, ILogger<ReintentoVerificacionIdentidadBuzonHostedService> logger)
    : BackgroundService
{
    /// <summary>
    /// Cada cuánto se mira qué conexiones toca reintentar. Es la granularidad del calendario, no el ritmo de llamadas a
    /// Graph: cada conexión lleva su propia hora de «no antes de», y un sondeo sin candidatas no llama a Microsoft.
    /// </summary>
    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        using var temporizador = new PeriodicTimer(IntervaloSondeo);

        while (await temporizador.WaitForNextTickAsync(stoppingToken))
            await IntentarReintentarTodosLosTenantsAsync(stoppingToken);
    }

    private async Task IntentarReintentarTodosLosTenantsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await eleccionLider.IntentarEjecutarComoLiderAsync("reintento-verificacion-identidad-buzon-microsoft365", ReintentarTodosLosTenantsAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló un ciclo de reintento de la verificación de identidad de los buzones.");
        }
    }

    private async Task ReintentarTodosLosTenantsAsync(CancellationToken stoppingToken)
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

            // Aislado por Tenant, igual que la renovación: un Tenant con un fallo inesperado no deja sin reintento a los
            // siguientes. Cada conexión va además aislada dentro de ReintentarAsync.
            try
            {
                using var ambito = ambitoFactory.CreateScope();
                using var _ = AmbitoTenantExplicito.Establecer(tenantId);
                await ambito.ServiceProvider.GetRequiredService<CompletarIdentidadBuzonService>()
                    .ReintentarAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Falló el reintento de verificación de identidad de los buzones del tenant {TenantId}; se continúa con el resto.",
                    tenantId);
            }
        }
    }
}