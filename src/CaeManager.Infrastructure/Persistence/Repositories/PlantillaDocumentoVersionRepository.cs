using CaeManager.Domain.Plantillas;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class PlantillaDocumentoVersionRepository(CaeManagerDbContext dbContext) : IPlantillaDocumentoVersionRepository
{
    public void Agregar(PlantillaDocumentoVersion version) => dbContext.PlantillasDocumentoVersion.Add(version);

    public Task<PlantillaDocumentoVersion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.PlantillasDocumentoVersion
            .Include(v => v.Elementos)
            .FirstOrDefaultAsync(v => v.Id == id, cancellationToken);

    public async Task<IReadOnlyList<PlantillaDocumentoVersion>> ObtenerPorPlantillaAsync(
        Guid plantillaDocumentoId, CancellationToken cancellationToken = default) =>
        await dbContext.PlantillasDocumentoVersion
            .Include(v => v.Elementos)
            .Where(v => v.PlantillaDocumentoId == plantillaDocumentoId)
            .OrderByDescending(v => v.NumeroVersion)
            .ToListAsync(cancellationToken);
}
