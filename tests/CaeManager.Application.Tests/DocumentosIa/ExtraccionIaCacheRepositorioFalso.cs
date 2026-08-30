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
}
