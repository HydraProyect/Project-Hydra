using MediatR;

namespace CaeManager.Application.Documentos.Eventos;

/// <summary>
/// Publicado por <c>ActualizarDocumentoDesdeAdjuntoCommandHandler</c> DESPUÉS
/// de confirmar la transacción — mismo patrón que <c>VisitaCreadaEvent</c>
/// (Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md § 6.5). Solo se publica desde ese flujo
/// (adjunto de conversación → Documento); un Documento creado o renovado
/// desde <c>/documentos</c> no tiene Conversacion de origen y no publica
/// nada. Su único efecto es la entrada del timeline: el adjunto ya quedó
/// asignado en el mismo guardado que el Documento.
/// </summary>
public sealed record DocumentoActualizadoEvent(Guid ConversacionId, Guid DocumentoId) : INotification;
