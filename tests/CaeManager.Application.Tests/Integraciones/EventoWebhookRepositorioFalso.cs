using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Tests.Integraciones;

public sealed class EventoWebhookRepositorioFalso : IEventoWebhookRepository
{
    public List<EventoWebhook> Eventos { get; } = [];

    public Task<EventoWebhook?> ReclamarSiguientePendienteAsync(ProveedorIntegracion proveedor, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<EventoWebhook>> ObtenerEstancadosAsync(
        ProveedorIntegracion proveedor, TimeSpan umbral, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<EventoWebhook>> ObtenerParaRedactarAsync(
        DateTime limiteUtc, int maximo, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<EventoWebhook>> ObtenerRetenidosAsync(Guid conexionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EventoWebhook>>(
            Eventos.Where(e => e.ConexionIntegracionId == conexionId && e.Estado == EstadoEventoWebhook.Retenido).ToList());

    public void Agregar(EventoWebhook evento) => Eventos.Add(evento);
}
