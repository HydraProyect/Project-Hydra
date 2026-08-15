using CaeManager.Domain.Plantillas;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class PlantillaDocumentoRepository(CaeManagerDbContext dbContext) : IPlantillaDocumentoRepository
{
    public void Agregar(PlantillaDocumento plantilla) => dbContext.PlantillasDocumento.Add(plantilla);

    public Task<PlantillaDocumento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.PlantillasDocumento.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
}
