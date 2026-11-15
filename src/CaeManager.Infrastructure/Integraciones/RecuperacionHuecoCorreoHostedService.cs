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
/// Recupera el hueco de correo de las conexiones de Microsoft 365 con la marca puesta (issue #2124), con
/// <see cref="RecuperacionHuecoCorreoService"/>. Envuelto en elección de líder, como
/// <see cref="RenovacionSuscripcionWebhookHostedService"/>. Solo recorre los Tenants propietarios activos, cada uno en
/// su propio ámbito con el Tenant explícito (filtro de Tenant y RLS del Tenant propietario); un Tenant que falla no
/// detiene a los demás.
/// </summary>
public class RecuperacionHuecoCorreoHostedService(
    IServiceScopeFactory ambitoFactory, IEleccionLiderService eleccionLider, ILogger<RecuperacionHuecoCorreoHostedService> logger)
    : BackgroundService
{
    /// <summary>
    /// Un hueco abierto es correo que no ha entrado: el sondeo es corto frente a la renovación diaria, y la pasada sobre
    /// una conexión sin marca no llama a Graph.
    /// </summary>
    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Lo que hace este servicio de fondo lo hace la plataforma, y la auditoría lo conserva como tal.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        using var temporizador = new PeriodicTimer(IntervaloSondeo);

        await IntentarRecuperarAsync(stoppingToken);

        while (await temporizador.WaitForNextTickAsync(stoppingToken))
            await IntentarRecuperarAsync(stoppingToken);
    }

    private async Task IntentarRecuperarAsync(CancellationToken stoppingToken)
    {
        try
        {
            await eleccionLider.IntentarEjecutarComoLiderAsync("recuperacion-hueco-correo-microsoft365", RecuperarTodosLosTenantsAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló un ciclo de recuperación del hueco de correo.");
        }
    }

    private async Task RecuperarTodosLosTenantsAsync(CancellationToken stoppingToken)
    {
        List<Guid> tenants;
        using (var ambito = ambitoFactory.CreateScope())
        {
            tenants = await ambito.ServiceProvider.GetRequiredService<ITenantsQueryContext>()
                .Tenants.Where(t => t.Estado == EstadoTenant.Activo)
                .Select(t => t.Id)
                .ToListAsync(stoppingToken);
        }

        foreach (var tenantId in tenants)
        {
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                using var ambito = ambitoFactory.CreateScope();
                using var _ = AmbitoTenantExplicito.Establecer(tenantId);
                await ambito.ServiceProvider.GetRequiredService<RecuperacionHuecoCorreoService>()
                    .RecuperarDelTenantActualAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Falló la recuperación del hueco de correo del tenant {TenantId}; se continúa con el resto de tenants en este ciclo.",
                    tenantId);
            }
        }
    }
}
