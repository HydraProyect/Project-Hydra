using CaeManager.Domain.Auditoria;

namespace CaeManager.Application.Auditoria;

public interface IAuditoriaQueryContext
{
    IQueryable<RegistroAuditoria> RegistrosAuditoria { get; }
}
