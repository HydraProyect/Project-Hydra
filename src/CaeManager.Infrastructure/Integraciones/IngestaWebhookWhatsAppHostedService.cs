using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Eventos;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Coordinacion;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Consumidor de la cola de <see cref="EventoWebhook"/> del proveedor
/// WhatsApp — clon de <see cref="IngestaWebhookHostedService"/> con dos
/// diferencias deliberadas: lock de líder propio ("ingesta-webhook-whatsapp",
/// para que un backlog de correo no retrase el chat) y espera híbrida
/// señal-o-tick (<see cref="ISenalIngestaWhatsApp"/>): el webhook despierta
/// al consumidor al persistir el evento, y el tick de 10 s es la red de
/// seguridad cuando la señal se pierde (reinicio, réplica no líder).
///
/// Publica <see cref="MensajeWhatsAppRecibidoEvent"/> vía IPublisher DESPUÉS
/// de SaveChanges de cada tanda — nunca antes del commit.
/// </summary>
public class IngestaWebhookWhatsAppHostedService(
    IServiceScopeFactory ambitoFactory,
    IEleccionLiderService eleccionLider,
    ISenalIngestaWhatsApp senal,
    ILogger<IngestaWebhookWhatsAppHostedService> logger)
    : BackgroundService
{
    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await eleccionLider.IntentarEjecutarComoLiderAsync("ingesta-webhook-whatsapp", SondearTodosLosTenantsAsync, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Un fallo en el propio sondeo no puede tumbar el BackgroundService entero — el siguiente ciclo lo reintenta.
                logger.LogError(ex, "Falló un ciclo de sondeo de la cola de ingesta de webhooks de WhatsApp.");
            }

            try
            {
                await senal.EsperarAsync(IntervaloSondeo, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
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
            var evento = await eventoRepositorio.ObtenerSiguientePendienteAsync(ProveedorIntegracion.WhatsApp, stoppingToken);
            if (evento is null) return;

            var ingesta = ambito.ServiceProvider.GetRequiredService<IngestaWebhookWhatsAppService>();
            var avisos = await ingesta.ProcesarAsync(evento, stoppingToken);

            await ambito.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(stoppingToken);

            // Tras el commit: los suscriptores (tiempo real de la UI) ya
            // pueden leer el mensaje. Un suscriptor que falle no debe frenar
            // la cola — cada publicación va en su propio try/catch.
            var publicador = ambito.ServiceProvider.GetRequiredService<IPublisher>();
            foreach (var aviso in avisos)
            {
                try
                {
                    await publicador.Publish(aviso, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Falló la publicación del aviso de mensaje WhatsApp de la conversación {ConversacionId}.",
                        aviso.ConversacionId);
                }
            }
        }
    }
}
