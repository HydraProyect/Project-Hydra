using CaeManager.Domain.DocumentosIa;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class AuditoriaExtraccionIaRepository(CaeManagerDbContext dbContext) : IAuditoriaExtraccionIaRepository
{
    public void Agregar(AuditoriaExtraccionIa auditoria) => dbContext.AuditoriasExtraccionIa.Add(auditoria);
}
