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
    // Por Id, no por clave compuesta: un Dictionary por clave sobrescribiría
    // en vez de coexistir cuando dos entidades DISTINTAS (dos Ids) comparten
    // la misma clave (hash+tipo+versión) — justo lo que hace falta simular
    // en la carrera de escritura (ver DocumentAIRouterServiceTests.
    // Vincula_al_documento_con_la_entrada_ganadora_...): la "perdedora" tiene
    // que poder existir en este almacén el tiempo suficiente para que
    // DescartarTrasConflicto la retire A ELLA y no a la "ganadora" que
    // comparte su misma clave.
    private readonly Dictionary<Guid, ExtraccionIaCache> _porId = [];
    private readonly List<ExtraccionIaCacheDocumento> _vinculos = [];

    public Task<ExtraccionIaCache?> ObtenerAsync(
        string hashSha256, string tipoEsperado, CancellationToken cancellationToken = default)
    {
        var tipoNormalizado = ExtraccionIaCache.NormalizarTipoEsperado(tipoEsperado);
        var encontrada = _porId.Values.FirstOrDefault(c =>
            c.HashSha256 == hashSha256 && c.TipoEsperado == tipoNormalizado && c.VersionPipeline == ExtraccionIaCache.VersionPipelineActual);
        return Task.FromResult(encontrada);
    }

    public void Agregar(ExtraccionIaCache cache) => _porId[cache.Id] = cache;

    public int VecesDescartada { get; private set; }

    /// <summary>
    /// Un falso en memoria no puede reproducir el choque real contra un
    /// índice único (eso lo cubre el test de integración con Postgres real)
    /// — pero sí tiene que ofrecer el método para que
    /// <see cref="DocumentAIRouterServiceTests"/> pueda simular el conflicto
    /// (con un <c>IUnitOfWork</c> falso que lanza <c>DbUpdateException</c> la
    /// primera vez) y comprobar que el router la retira antes de reintentar.
    /// Por Id, no por clave compuesta: retirar por clave borraría la entrada
    /// GANADORA si comparte esa misma clave con la que se está descartando.
    /// </summary>
    public void DescartarTrasConflicto(ExtraccionIaCache cache)
    {
        _porId.Remove(cache.Id);
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

            _porId.Remove(cacheId);
        }

        return Task.CompletedTask;
    }
}
