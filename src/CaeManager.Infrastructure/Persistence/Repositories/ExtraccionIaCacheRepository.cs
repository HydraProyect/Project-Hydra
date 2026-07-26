using CaeManager.Domain.DocumentosIa;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ExtraccionIaCacheRepository(CaeManagerDbContext dbContext) : IExtraccionIaCacheRepository
{
    public Task<ExtraccionIaCache?> ObtenerPorHashAsync(string hashSha256, CancellationToken cancellationToken = default) =>
        dbContext.ExtraccionesIaCache.FirstOrDefaultAsync(c => c.HashSha256 == hashSha256, cancellationToken);

    public void Agregar(ExtraccionIaCache cache) => dbContext.ExtraccionesIaCache.Add(cache);
}
