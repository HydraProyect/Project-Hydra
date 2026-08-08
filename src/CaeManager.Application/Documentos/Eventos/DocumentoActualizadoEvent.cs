using MediatR;

namespace CaeManager.Application.Documentos.Eventos;

/// <summary>
/// Publicado por <c>ActualizarDocumentoDesdeAdjuntoCommandHandler</c> DESPUÉS
/// de confirmar la transacción — mismo patrón que <c>VisitaCreadaEvent</c>
/// (ARQUITECTURA-INTEGRACIONES.md § 6.5). Solo se publica desde ese flujo
/// (adjunto de conversación → Documento); un Documento creado o renovado
/// desde <c>/documentos</c> no tiene Conversacion de origen y no publica
/// nada.
/// </summary>
public sealed record DocumentoActualizadoEvent(Guid ConversacionId, Guid DocumentoId) : INotification;
