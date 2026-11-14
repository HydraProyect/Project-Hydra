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
    /// auditoría — no una lectura de negocio más.
    /// </summary>
    IQueryable<RegistroAccesoDocumentoSensible> RegistrosAccesoDocumentoSensible { get; }
}
