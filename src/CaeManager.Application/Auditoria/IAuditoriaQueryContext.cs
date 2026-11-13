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
    /// la fila «Revisión IA» de una revisión y persona antes de escribir otra.
    /// </summary>
    IQueryable<RegistroAccesoDocumentoSensible> RegistrosAccesoDocumentoSensible { get; }
}
