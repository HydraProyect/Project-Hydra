using CaeManager.Domain.Centros;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class CanalGestionDocumentalRepository(CaeManagerDbContext dbContext) : ICanalGestionDocumentalRepository
{
    public async Task<CanalGestionDocumental?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.CanalesGestionDocumental.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<IReadOnlyList<CanalGestionDocumental>> ObtenerPorCentroAsync(
        Guid centroId, CancellationToken cancellationToken = default) =>
        await dbContext.CanalesGestionDocumental.Where(c => c.CentroId == centroId).ToListAsync(cancellationToken);

    public void Agregar(CanalGestionDocumental canal) => dbContext.CanalesGestionDocumental.Add(canal);
}
