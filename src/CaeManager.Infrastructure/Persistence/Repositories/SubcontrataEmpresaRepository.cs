using CaeManager.Domain.Subcontratas;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class SubcontrataEmpresaRepository(CaeManagerDbContext dbContext) : ISubcontrataEmpresaRepository
{
    public async Task<IReadOnlyList<SubcontrataEmpresa>> ObtenerPorSubcontrataAsync(Guid subcontrataId, CancellationToken cancellationToken = default) =>
        await dbContext.Set<SubcontrataEmpresa>().Where(se => se.SubcontrataId == subcontrataId).ToListAsync(cancellationToken);

    public void Agregar(SubcontrataEmpresa subcontrataEmpresa) => dbContext.Set<SubcontrataEmpresa>().Add(subcontrataEmpresa);

    public void Eliminar(SubcontrataEmpresa subcontrataEmpresa) => dbContext.Set<SubcontrataEmpresa>().Remove(subcontrataEmpresa);
}
