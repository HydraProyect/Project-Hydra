using MediatR;

namespace CaeManager.Application.Visitas.Eventos;

/// <summary>
/// Publicado por <c>CrearVisitaCommandHandler</c> DESPUÉS de confirmar la
/// transacción (mismo patrón que <c>MensajeWhatsAppRecibidoEvent</c> —
/// ARQUITECTURA-INTEGRACIONES.md § 6.5: "el orquestador publica, no
/// invoca") — solo cuando la Visita se creó desde una <c>SugerenciaVisitaCorreo</c>
/// y se pudo resolver a qué Conversacion pertenece. Una Visita creada
/// directamente desde <c>/visitas</c> (sin sugerencia) no tiene hilo al que
/// avisar y no publica nada. Sin TenantId explícito: a diferencia de
/// <c>MensajeWhatsAppRecibidoEvent</c> (publicado desde un hosted service que
/// cambia de tenant en cada iteración), esto se publica en el mismo ámbito
/// scoped que ya resolvió el tenant de la petición — el sellado de
/// <c>EventoConversacion</c> lo hace el interceptor de tenant, no este evento.
/// </summary>
public sealed record VisitaCreadaEvent(Guid ConversacionId, Guid VisitaId) : INotification;
