using CaeManager.Domain.DocumentosIa;

namespace CaeManager.Application.Tests.DocumentosIa;

/// <summary>
/// Indexa por la clave COMPLETA (hash + tipo esperado normalizado + versión de
/// pipeline), igual que el índice único real. Un falso que siguiera indexando
/// solo por hash serviría la entrada de un tipo cuando se pide otro y dejaría
/// pasar en verde justo el defecto que la clave nueva cierra.
/// </summary>
public class ExtraccionIaCacheRepositorioFalso : IExtraccionIaCacheRepository
{
    private readonly Dictionary<(string Hash, string Tipo, string Version), ExtraccionIaCache> _porClave = [];
    private readonly List<ExtraccionIaCacheDocumento> _vinculos = [];

    public Task<ExtraccionIaCache?> ObtenerAsync(
        string hashSha256, string tipoEsperado, CancellationToken cancellationToken = default) =>
        Task.FromResult(_porClave.GetValueOrDefault(
            (hashSha256, ExtraccionIaCache.NormalizarTipoEsperado(tipoEsperado), ExtraccionIaCache.VersionPipelineActual)));

    public void Agregar(ExtraccionIaCache cache) =>
        _porClave[(cache.HashSha256, cache.TipoEsperado, cache.VersionPipeline)] = cache;

    public int VecesDescartada { get; private set; }

    /// <summary>
    /// Un falso en memoria no puede reproducir el choque real contra un
    /// índice único (eso lo cubre el test de integración con Postgres real)
    /// — pero sí tiene que ofrecer el método para que
    /// <see cref="DocumentAIRouterServiceTests"/> pueda simular el conflicto
    /// (con un <c>IUnitOfWork</c> falso que lanza <c>DbUpdateException</c> la
    /// primera vez) y comprobar que el router la retira antes de reintentar.
    /// </summary>
    public void DescartarTrasConflicto(ExtraccionIaCache cache)
    {
        _porClave.Remove((cache.HashSha256, cache.TipoEsperado, cache.VersionPipeline));
        VecesDescartada++;
    }

    /// <summary>REC-036/DEC-34 — vínculos creados hasta ahora, para que los tests los inspeccionen directamente.</summary>
    public IReadOnlyList<ExtraccionIaCacheDocumento> Vinculos => _vinculos;

    public Task<ExtraccionIaCacheDocumento?> VincularDocumentoAsync(
        Guid extraccionIaCacheId, Guid documentoId, CancellationToken cancellationToken = default)
    {
        if (_vinculos.Any(v => v.ExtraccionIaCacheId == extraccionIaCacheId && v.DocumentoId == documentoId))
            return Task.FromResult<ExtraccionIaCacheDocumento?>(null);

        var vinculo = ExtraccionIaCacheDocumento.Crear(extraccionIaCacheId, documentoId);
        _vinculos.Add(vinculo);
        return Task.FromResult<ExtraccionIaCacheDocumento?>(vinculo);
    }

    public int VecesVinculoDescartado { get; private set; }

    public void DescartarVinculoTrasConflicto(ExtraccionIaCacheDocumento vinculo)
    {
        _vinculos.Remove(vinculo);
        VecesVinculoDescartado++;
    }

    public Task PurgarVinculadosADocumentosAsync(IReadOnlyCollection<Guid> documentoIds, CancellationToken cancellationToken = default)
    {
        var vinculosDelLote = _vinculos.Where(v => documentoIds.Contains(v.DocumentoId)).ToList();
        var cacheIdsAfectados = vinculosDelLote.Select(v => v.ExtraccionIaCacheId).Distinct().ToList();

        foreach (var vinculo in vinculosDelLote)
            _vinculos.Remove(vinculo);

        foreach (var cacheId in cacheIdsAfectados)
        {
            if (_vinculos.Any(v => v.ExtraccionIaCacheId == cacheId))
                continue; // otro Documento fuera del lote sigue usando esta entrada.

            var entrada = _porClave.Values.FirstOrDefault(c => c.Id == cacheId);
            if (entrada is not null)
                _porClave.Remove((entrada.HashSha256, entrada.TipoEsperado, entrada.VersionPipeline));
        }

        return Task.CompletedTask;
    }
}
