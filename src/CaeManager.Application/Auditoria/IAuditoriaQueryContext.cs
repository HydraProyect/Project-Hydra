using CaeManager.Domain.Auditoria;

namespace CaeManager.Application.Auditoria;

public interface IAuditoriaQueryContext
{
    IQueryable<RegistroAuditoria> RegistrosAuditoria { get; }

    /// <summary>
    /// Rastro de acceso a documentos sensibles (DEC-36, REC-099). Vive aquí y
    /// no en un contexto propio porque la consulta que lo sirve
    /// (<c>ObtenerAccesosDocumentosSensiblesQuery</c>) es, igual que
    /// <c>ObtenerAuditoriaQuery</c>, una superficie administrativa de
    /// auditoría — no una lectura de negocio más. El único otro lector es
    /// <c>RegistroAccesoDocumentoSensibleService</c>, que solo pregunta si ya consta
    /// la fila «Revisión IA» de un Documento y persona en el Tenant actual, desde que
    /// se creó la revisión pendiente, antes de escribir otra: como mucho una fila por
    /// persona y revisión pendiente: con una sola revisión pendiente del Documento se
    /// deduplica por persona y Documento desde que se creó; con varias, cada acceso deja
    /// fila (y no llega a preguntar); peticiones simultáneas pueden dejar más, nunca
    /// menos. Solo decide la fila: cada acceso cuenta para la señal de extracción masiva.
    /// </summary>
    IQueryable<RegistroAccesoDocumentoSensible> RegistrosAccesoDocumentoSensible { get; }
}
