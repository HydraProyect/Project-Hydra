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

    /// <summary>
    /// El doble del sondeo (auditoría de colas, 2026-08-30): con ventana =
    /// intervalo de sondeo, un solo ciclo fallido (excepción, redeploy a
    /// mitad de ciclo) dejaba la siguiente ejecución, 24 h después, mirando
    /// una suscripción que para entonces ya había expirado de verdad —
    /// margen operativo prácticamente nulo ante cualquier fallo puntual.
    /// 48 h sigue cómodo bajo el máximo de Graph (~70.5 h): un ciclo se
    /// puede perder entero y el siguiente todavía la encuentra dentro de la
    /// ventana.
    /// </summary>
    private static readonly TimeSpan VentanaRenovacion = TimeSpan.FromHours(48);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // P41c: todo lo que haga este servicio de fondo lo hace la propia
        // plataforma, y la auditoría lo conserva como tal. Se declara aquí, en el
        // punto de entrada, y no alrededor de cada guardado: así ningún camino
        // interno —ni uno añadido después— se queda fuera por olvido.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

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

            // Aislado igual que ProcesadorAnalisisDocumentoHostedService: sin
            // este try/catch, un tenant con una conexión rota (token de
            // refresco inválido, Graph devolviendo un error persistente)
            // abortaba este foreach entero y dejaba sin renovar a los
            // siguientes — con una ventana de renovación de 24 h y un sondeo
            // diario, eso podía costarle a un tenant sano toda su ventana de
            // margen antes de Graph expirar la suscripción de verdad.
            try
            {
                await RenovarPendientesDelTenantAsync(tenantId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Falló la renovación de suscripciones de webhook del tenant {TenantId}; se continúa con el resto de tenants en este ciclo.",
                    tenantId);
            }
        }
    }

    private async Task RenovarPendientesDelTenantAsync(Guid tenantId, CancellationToken stoppingToken)
    {
        // En su propio ámbito y antes de renovar: un choque de identidad descarta los cambios en memoria de su
        // DbContext, y no debe llevarse por delante los de la renovación. Un fallo aquí no salta la renovación
        // del Tenant (dos ciclos perdidos dejarían expirar una suscripción): solo deja sin verificar sus buzones.
        // La comprobación rehabilita por sí misma la conexión en ConError que verifica en este ciclo, esté o no su
        // suscripción en la ventana de renovación: aquí ya no se rehabilita nada.
        try
        {
            using var ambitoIdentidad = ambitoFactory.CreateScope();
            using var __ = AmbitoTenantExplicito.Establecer(tenantId);
            await ambitoIdentidad.ServiceProvider.GetRequiredService<CompletarIdentidadBuzonService>()
                .CompletarAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló la comprobación de identidad de los buzones del tenant {TenantId}; se renueva sin rehabilitar ninguno.", tenantId);
        }

        // Cada conexión se guarda por separado y aislada, y una colisión de concurrencia se resuelve recargando y
        // volviendo a aplicar lo observado: ver RenovacionSuscripcionesWebhookService. Un fallo transitorio (429, 5xx,
        // red, tiempo agotado) se reintenta en el siguiente ciclo; uno definitivo marca el hueco y pasa la conexión a
        // ConError (regla en CompletarIdentidadBuzonService.MarcarConErrorDeRenovacion).
        using var ambito = ambitoFactory.CreateScope();
        using var _ = AmbitoTenantExplicito.Establecer(tenantId);
        await ambito.ServiceProvider.GetRequiredService<RenovacionSuscripcionesWebhookService>()
            .RenovarProximasAExpirarAsync(VentanaRenovacion, stoppingToken);
    }
}
