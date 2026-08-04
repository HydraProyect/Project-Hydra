namespace CaeManager.Domain.Integraciones;

public interface IEventoWebhookRepository
{
    /// <summary>
    /// Siguiente evento pendiente cuya conexión pertenece al proveedor
    /// indicado — cada consumidor (Microsoft 365, WhatsApp) drena SOLO su
    /// cola, para que un backlog de correo no retrase el chat ni al revés.
    /// </summary>
    Task<EventoWebhook?> ObtenerSiguientePendienteAsync(ProveedorIntegracion proveedor, CancellationToken cancellationToken = default);

    void Agregar(EventoWebhook evento);
}
