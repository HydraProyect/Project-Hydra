using CaeManager.Domain.Plantillas;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class LoteGeneracionDocumentoRepository(CaeManagerDbContext dbContext) : ILoteGeneracionDocumentoRepository
{
    public void Agregar(LoteGeneracionDocumento lote) => dbContext.LotesGeneracionDocumento.Add(lote);

    public Task<LoteGeneracionDocumento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.LotesGeneracionDocumento.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
}
