namespace CaeManager.Application.Comunicaciones.Eventos;

/// <summary>
/// Pub-sub en memoria hacia los circuitos Blazor Server: un componente se
/// suscribe con el tenant de su sesión y recibe los avisos de mensaje
/// WhatsApp nuevo que la ingesta publica desde el job de fondo — es lo que
/// hace que el chat se actualice sin recargar.
///
/// El <see cref="IDisposable"/> de <see cref="Suscribir"/> es crítico: los
/// circuitos Blazor mueren sin avisar (pestaña cerrada, red caída) y sin el
/// dispose la lista de suscriptores crecería para siempre.
///
/// Costura multi-réplica: la implementación actual (singleton en proceso,
/// ver Infrastructure/Coordinacion) solo avisa a los circuitos de la réplica
/// donde corre el consumidor líder; con varias réplicas, los circuitos de
/// las demás se enteran al navegar. Cuando haga falta, se reimplementa sobre
/// el backplane Redis (SignalRRedisOptions ya modelado) sin tocar llamadores.
/// </summary>
public interface INotificadorMensajesTiempoReal
{
    IDisposable Suscribir(Guid tenantId, Func<MensajeWhatsAppRecibidoEvent, Task> callback);

    Task PublicarAsync(MensajeWhatsAppRecibidoEvent aviso, CancellationToken cancellationToken = default);
}
