using MediatR;

namespace CaeManager.Application.Documentos.Eventos;

/// <summary>
/// Acaba de nacer un Documento: un alta (<see cref="SustituyeADocumentoId"/> null) o una renovación con archivo
/// nuevo, que crea un Documento nuevo y sustituye al anterior (<see cref="SustituyeADocumentoId"/>). Se publica
/// DESPUÉS de confirmar la transacción, cuando sus acreditaciones en plataforma ya están guardadas.
///
/// <para>
/// A diferencia de <see cref="DocumentacionCambiadaEvent"/>, que también cubre la corrección de fechas sin
/// archivo y la aplicación de una detección IA, este distingue el acto que hace nacer una Gestión automática
/// (especificación «Gestión de varios pasos», § 4.1): la corrección de fechas no lo publica, porque no nace
/// ningún Documento.
/// </para>
/// </summary>
public sealed record DocumentoNacidoEvent(Guid DocumentoId, Guid? SustituyeADocumentoId = null) : INotification;
