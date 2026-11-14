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
    /// la fila «Revisión IA» de un Documento y persona, desde que se creó la revisión
    /// pendiente y dentro de la ventana de detección de extracción masiva, antes de
    /// escribir otra: como mucho una fila por persona y Documento por cada revisión
    /// pendiente y dentro de esa ventana; peticiones simultáneas pueden dejar más,
    /// nunca menos.
    /// </summary>
    IQueryable<RegistroAccesoDocumentoSensible> RegistrosAccesoDocumentoSensible { get; }
}
