using MediatR;

namespace CaeManager.Application.Reclamaciones.Eventos;

/// <summary>
/// Publicado por <c>EnviarReclamacionCommandHandler</c> DESPUÉS de confirmar la
/// transacción — mismo patrón que <c>VisitaCreadaEvent</c> y
/// <c>DocumentoActualizadoEvent</c> (ARQUITECTURA-INTEGRACIONES.md § 6.5: "el
/// orquestador publica, no invoca"). Solo se publica cuando la reclamación
/// salió por un buzón conectado y por tanto tiene una <c>Conversacion</c> a la
/// que avisar; una reclamación enviada por <c>IEmailService</c> (sin buzón) no
/// tiene hilo y no publica nada.
///
/// Sin TenantId explícito: se publica en el mismo ámbito scoped que ya resolvió
/// el tenant de la petición, y el sellado de <c>EventoConversacion</c> lo hace
/// el interceptor de tenant.
/// </summary>
public sealed record ReclamacionEnviadaEvent(Guid ConversacionId, Guid ReclamacionId) : INotification;
