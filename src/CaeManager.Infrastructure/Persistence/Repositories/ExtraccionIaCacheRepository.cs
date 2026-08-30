using CaeManager.Domain.DocumentosIa;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ExtraccionIaCacheRepository(CaeManagerDbContext dbContext) : IExtraccionIaCacheRepository
{
    public Task<ExtraccionIaCache?> ObtenerAsync(
        string hashSha256, string tipoEsperado, CancellationToken cancellationToken = default)
    {
        // La normalización del tipo tiene que ser la MISMA que usa
        // ExtraccionIaCache.Crear al escribir. Si divergieran, la caché no
        // acertaría nunca y el fallo sería mudo: todo seguiría funcionando,
        // solo que pagando cada extracción dos veces. Por eso la normalización
        // vive en la entidad y no aquí.
        var tipoNormalizado = ExtraccionIaCache.NormalizarTipoEsperado(tipoEsperado);

        return dbContext.ExtraccionesIaCache.FirstOrDefaultAsync(
            c => c.HashSha256 == hashSha256
                 && c.TipoEsperado == tipoNormalizado
                 && c.VersionPipeline == ExtraccionIaCache.VersionPipelineActual,
            cancellationToken);
    }

    public void Agregar(ExtraccionIaCache cache) => dbContext.ExtraccionesIaCache.Add(cache);
}
