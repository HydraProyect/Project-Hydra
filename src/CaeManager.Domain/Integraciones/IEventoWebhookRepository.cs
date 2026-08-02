namespace CaeManager.Domain.Integraciones;

public interface IEventoWebhookRepository
{
    Task<EventoWebhook?> ObtenerSiguientePendienteAsync(CancellationToken cancellationToken = default);

    void Agregar(EventoWebhook evento);
}
