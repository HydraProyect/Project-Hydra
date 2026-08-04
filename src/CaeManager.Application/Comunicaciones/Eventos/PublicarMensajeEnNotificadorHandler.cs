using MediatR;

namespace CaeManager.Application.Comunicaciones.Eventos;

/// <summary>
/// Puente entre el evento de integración (§ 6.5: la ingesta publica, no
/// invoca) y el pub-sub de circuitos Blazor. Vive en Application porque
/// MediatR solo escanea este ensamblado — la implementación real del
/// notificador la aporta Infrastructure por DI.
/// </summary>
public class PublicarMensajeEnNotificadorHandler(INotificadorMensajesTiempoReal notificador)
    : INotificationHandler<MensajeWhatsAppRecibidoEvent>
{
    public Task Handle(MensajeWhatsAppRecibidoEvent notification, CancellationToken cancellationToken) =>
        notificador.PublicarAsync(notification, cancellationToken);
}
