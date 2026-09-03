using CaeManager.Domain.Auditoria;

namespace CaeManager.Application.Common;

/// <summary>
/// DEC-36 (REC-099): registra el acceso efectivo al contenido de un
/// <see cref="Domain.Documentos.Documento"/> clasificado como sensible.
///
/// <para>
/// <b>Solo llamar desde los puntos de servicio que sirven contenido
/// clasificable a un actor humano</b> — los ocho medidos en HO-099-01 § 6,
/// de los cuales tres tienen clasificación
/// real: el archivo vigente y la versión anterior desde Auditoría (instancia
/// de <c>Documento</c>, clasificable por
/// <see cref="Domain.Documentos.TipoDocumento.Sensibilidad"/>), y la
/// evidencia de verificación de subcontrata (<c>TipoDocumentoId</c> propio,
/// sin ser un <c>Documento</c> — ver la segunda sobrecarga). Los otros cinco
/// sirven contenido sin ninguna clasificación (adjunto de correo, imagen de
/// firma/sello, plantilla en blanco) y no deben llamar a este servicio — ver el comentario de cada punto de
/// servicio y el veredicto completo en el RETURN PACKAGE de HO-099-01.
/// </para>
///
/// <para>
/// No es un interceptor de consultas —DEC-36 lo prohíbe expresamente— sino
/// una llamada explícita en capa de aplicación, con contexto suficiente para
/// saber quién accede, a qué Documento y bajo qué vía.
/// </para>
/// </summary>
public interface IRegistroAccesoDocumentoSensibleService
{
    /// <summary>
    /// Resuelve la categoría vigente del Documento y registra el acceso solo
    /// si es sensible (<c>Sensibilidad != SinDatosPersonales</c>) — nunca
    /// para documentos sin datos personales, DEC-36 lo prohíbe («nunca
    /// registrar de más»). Si el Documento ya no puede resolverse (baja
    /// física, caso raro fuera de la retención ordinaria — ver
    /// CalculadoraRetencionDocumento), se registra igual con la categoría
    /// más protectora: preferir un registro de más aquí antes que perder en
    /// silencio un acceso a lo que pudo haber sido un dato de salud (mismo
    /// criterio que el valor por defecto de <c>TipoDocumento.Sensibilidad</c>
    /// para un tipo no clasificado, REC-132).
    /// </summary>
    Task RegistrarSiSensibleAsync(
        Guid documentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default);

    /// <summary>
    /// Variante para contenido clasificado que <b>no</b> es una instancia de
    /// <see cref="Domain.Documentos.Documento"/> — hoy solo
    /// <c>VerificacionExternaSubcontrata</c> (ADR-005 § 2.3): tiene su propio
    /// <c>TipoDocumentoId</c> real (la evidencia adjunta no es una plantilla en
    /// blanco), así que la categoría se resuelve directamente sin el primer
    /// roundtrip de <see cref="RegistrarSiSensibleAsync(Guid, TipoAccesoDocumentoSensible, CancellationToken)"/>.
    /// <paramref name="recursoId"/> viaja en el campo <c>DocumentoId</c> del
    /// registro (sin FK, ver el comentario de la entidad) — referencia al
    /// recurso accedido, no necesariamente a un <c>Documento</c>.
    /// </summary>
    Task RegistrarSiSensibleAsync(
        Guid recursoId, Guid tipoDocumentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default);
}
