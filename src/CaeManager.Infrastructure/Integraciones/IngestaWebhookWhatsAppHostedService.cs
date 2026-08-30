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

    /// <summary>Mismo umbral que <c>IngestaWebhookHostedService.UmbralEstancado</c>.</summary>
    private static readonly TimeSpan UmbralEstancado = TimeSpan.FromMinutes(15);

    /// <summary>Ver el comentario del mismo nombre en <c>IngestaWebhookHostedService</c>.</summary>
    private const int LoteMaximoPorTenant = 50;

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

            // Aislado igual que en IngestaWebhookHostedService — sin esto,
            // un tenant con un problema persistente (p. ej. token de
            // WhatsApp caducado) bloqueaba a los siguientes en cada tick.
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
                    "Falló el sondeo de la cola de ingesta de webhooks de WhatsApp del tenant {TenantId}; se continúa con el resto de tenants en este tick.",
                    tenantId);
            }
        }
    }

    private async Task ProcesarPendientesDelTenantAsync(Guid tenantId, CancellationToken stoppingToken)
    {
        await RecuperarEstancadosAsync(tenantId, stoppingToken);

        var procesadosEnEsteTick = 0;

        while (!stoppingToken.IsCancellationRequested && procesadosEnEsteTick < LoteMaximoPorTenant)
        {
            using var ambito = ambitoFactory.CreateScope();
            using var _ = AmbitoTenantExplicito.Establecer(tenantId);

            var eventoRepositorio = ambito.ServiceProvider.GetRequiredService<IEventoWebhookRepository>();
            var evento = await eventoRepositorio.ReclamarSiguientePendienteAsync(ProveedorIntegracion.WhatsApp, stoppingToken);
            if (evento is null) return;

            procesadosEnEsteTick++;

            var ingesta = ambito.ServiceProvider.GetRequiredService<IngestaWebhookWhatsAppService>();
            IReadOnlyList<MensajeWhatsAppRecibidoEvent> avisos;
            try
            {
                avisos = await ingesta.ProcesarAsync(evento, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Apagado normal — ver IngestaWebhookHostedService para el
                // mismo razonamiento. CancellationToken.None a propósito.
                evento.DevolverAPendienteTrasCancelacion();
                await ambito.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
                throw;
            }

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

    private async Task RecuperarEstancadosAsync(Guid tenantId, CancellationToken stoppingToken)
    {
        using var ambito = ambitoFactory.CreateScope();
        using var _ = AmbitoTenantExplicito.Establecer(tenantId);

        var repositorio = ambito.ServiceProvider.GetRequiredService<IEventoWebhookRepository>();
        var estancados = await repositorio.ObtenerEstancadosAsync(ProveedorIntegracion.WhatsApp, UmbralEstancado, stoppingToken);
        if (estancados.Count == 0) return;

        var ahora = DateTime.UtcNow;
        foreach (var evento in estancados)
        {
            logger.LogWarning(
                "Evento de webhook de WhatsApp {EventoId} (tenant {TenantId}) llevaba más de {Umbral} en \"Procesando\" — se recupera.",
                evento.Id, tenantId, UmbralEstancado);
            evento.RecuperarSiEstancado(UmbralEstancado, ahora);
        }

        await ambito.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(stoppingToken);
    }
}
