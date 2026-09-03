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

    public void DescartarTrasConflicto(ExtraccionIaCache cache) => dbContext.Entry(cache).State = EntityState.Detached;

    public async Task<ExtraccionIaCacheDocumento?> VincularDocumentoAsync(
        Guid extraccionIaCacheId, Guid documentoId, CancellationToken cancellationToken = default)
    {
        var yaVinculado = await dbContext.ExtraccionesIaCacheDocumentos.AnyAsync(
            v => v.ExtraccionIaCacheId == extraccionIaCacheId && v.DocumentoId == documentoId, cancellationToken);
        if (yaVinculado)
            return null;

        var vinculo = ExtraccionIaCacheDocumento.Crear(extraccionIaCacheId, documentoId);
        dbContext.ExtraccionesIaCacheDocumentos.Add(vinculo);
        return vinculo;
    }

    public void DescartarVinculoTrasConflicto(ExtraccionIaCacheDocumento vinculo) =>
        dbContext.Entry(vinculo).State = EntityState.Detached;

    public async Task PurgarVinculadosADocumentosAsync(
        IReadOnlyCollection<Guid> documentoIds, CancellationToken cancellationToken = default)
    {
        if (documentoIds.Count == 0)
            return;

        var vinculosDelLote = await dbContext.ExtraccionesIaCacheDocumentos
            .Where(v => documentoIds.Contains(v.DocumentoId))
            .ToListAsync(cancellationToken);

        if (vinculosDelLote.Count == 0)
            return;

        var cacheIdsAfectados = vinculosDelLote.Select(v => v.ExtraccionIaCacheId).Distinct().ToList();

        // Consulta contra base ANTES de quitar nada del tracker: como filtra
        // "documentoIds.Contains" es false, nunca mira las filas que
        // RemoveRange va a marcar más abajo, así que da igual que ese borrado
        // todavía no se haya volcado con SaveChangesAsync — no hay ventana de
        // carrera entre las dos consultas de este método.
        var cacheIdsConVinculoFueraDelLote = await dbContext.ExtraccionesIaCacheDocumentos
            .Where(v => cacheIdsAfectados.Contains(v.ExtraccionIaCacheId) && !documentoIds.Contains(v.DocumentoId))
            .Select(v => v.ExtraccionIaCacheId)
            .Distinct()
            .ToListAsync(cancellationToken);

        dbContext.ExtraccionesIaCacheDocumentos.RemoveRange(vinculosDelLote);

        var cacheIdsHuerfanos = cacheIdsAfectados.Except(cacheIdsConVinculoFueraDelLote).ToList();
        if (cacheIdsHuerfanos.Count == 0)
            return;

        var cachesHuerfanas = await dbContext.ExtraccionesIaCache
            .Where(c => cacheIdsHuerfanos.Contains(c.Id))
            .ToListAsync(cancellationToken);

        dbContext.ExtraccionesIaCache.RemoveRange(cachesHuerfanas);
    }
}
