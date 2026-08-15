using CaeManager.Domain.DocumentosIa;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class AuditoriaExtraccionIaRepository(CaeManagerDbContext dbContext) : IAuditoriaExtraccionIaRepository
{
    public void Agregar(AuditoriaExtraccionIa auditoria) => dbContext.AuditoriasExtraccionIa.Add(auditoria);

    public async Task<AuditoriaExtraccionIa?> ObtenerUltimaSinDecisionPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default) =>
        await dbContext.AuditoriasExtraccionIa
            .Where(a => a.DocumentoId == documentoId && a.DecisionHumana == null)
            .OrderByDescending(a => a.CreadaEnUtc)
            .FirstOrDefaultAsync(cancellationToken);
}
