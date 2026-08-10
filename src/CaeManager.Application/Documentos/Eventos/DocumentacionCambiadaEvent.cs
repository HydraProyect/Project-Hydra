using MediatR;

namespace CaeManager.Application.Documentos.Eventos;

/// <summary>
/// Un Documento acaba de nacer o de renovarse. Se publica DESPUÉS de confirmar
/// la transacción (ARQUITECTURA-INTEGRACIONES.md § 6.5) desde los flujos que
/// pueden dejar completo el expediente de una Visita: alta, renovación,
/// aplicación de una detección IA y actualización desde un adjunto de correo.
///
/// A diferencia de <see cref="DocumentoActualizadoEvent"/> (que existe para el
/// timeline de una Conversacion concreta y por eso lleva su Id), este no
/// presupone ningún hilo de origen: interesa el Documento, venga de donde venga.
///
/// No se publica al eliminar ni al restaurar: borrar documentación solo puede
/// romper la completitud, y el sello del expediente es de un solo sentido.
/// </summary>
public sealed record DocumentacionCambiadaEvent(Guid DocumentoId) : INotification;
