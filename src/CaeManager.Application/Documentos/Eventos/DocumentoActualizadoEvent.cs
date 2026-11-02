using MediatR;

namespace CaeManager.Application.Documentos.Eventos;

/// <summary>
/// Publicado por <c>ActualizarDocumentoDesdeAdjuntoCommandHandler</c> DESPUÉS
/// de confirmar la transacción — mismo patrón que <c>VisitaCreadaEvent</c>
/// (Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md § 6.5). Solo se publica desde ese flujo
/// (adjunto de conversación → Documento); un Documento creado o renovado
/// desde <c>/documentos</c> no tiene Conversacion de origen y no publica
/// nada.
///
/// Lleva el destino y el tipo con los que se dio el alta, no los que el adjunto tenga al consumirse el evento. Con
/// <paramref name="AplicaPropuestaFijada"/> el alta salió de la propuesta guardada en el adjunto (asignación por lote)
/// y no de una elección de la persona en ese momento: si la propuesta ya es otra, el adjunto no se marca.
/// </summary>
public sealed record DocumentoActualizadoEvent(
    Guid ConversacionId, Guid DocumentoId, Guid AdjuntoId, Guid? TrabajadorId, Guid? EmpresaId, Guid TipoDocumentoId,
    bool AplicaPropuestaFijada = false) : INotification;
