using CaeManager.Domain.Subcontratas;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class CredencialAccesoSubcontrataRepository(CaeManagerDbContext dbContext) : ICredencialAccesoSubcontrataRepository
{
    public Task<CredencialAccesoSubcontrata?> ObtenerPorSubcontrataAsync(Guid subcontrataId, CancellationToken cancellationToken = default) =>
        dbContext.CredencialesAccesoSubcontrata.FirstOrDefaultAsync(c => c.SubcontrataId == subcontrataId, cancellationToken);

    public void Agregar(CredencialAccesoSubcontrata credencial) => dbContext.CredencialesAccesoSubcontrata.Add(credencial);
}
