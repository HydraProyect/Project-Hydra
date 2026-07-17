using CaeManager.Domain.Subcontratas;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class SubcontrataClienteRepository(CaeManagerDbContext dbContext) : ISubcontrataClienteRepository
{
    public async Task<IReadOnlyList<SubcontrataCliente>> ObtenerPorSubcontrataAsync(Guid subcontrataId, CancellationToken cancellationToken = default) =>
        await dbContext.Set<SubcontrataCliente>().Where(sc => sc.SubcontrataId == subcontrataId).ToListAsync(cancellationToken);

    public void Agregar(SubcontrataCliente subcontrataCliente) => dbContext.Set<SubcontrataCliente>().Add(subcontrataCliente);

    public void Eliminar(SubcontrataCliente subcontrataCliente) => dbContext.Set<SubcontrataCliente>().Remove(subcontrataCliente);
}
