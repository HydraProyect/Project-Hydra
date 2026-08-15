using CaeManager.Domain.Plantillas;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ItemGeneracionDocumentoRepository(CaeManagerDbContext dbContext) : IItemGeneracionDocumentoRepository
{
    public void Agregar(ItemGeneracionDocumento item) => dbContext.ItemsGeneracionDocumento.Add(item);

    public Task<ItemGeneracionDocumento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ItemsGeneracionDocumento.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
}
