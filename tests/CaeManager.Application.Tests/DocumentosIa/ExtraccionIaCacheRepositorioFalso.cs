using CaeManager.Domain.DocumentosIa;

namespace CaeManager.Application.Tests.DocumentosIa;

public class ExtraccionIaCacheRepositorioFalso : IExtraccionIaCacheRepository
{
    private readonly Dictionary<string, ExtraccionIaCache> _porHash = [];

    public Task<ExtraccionIaCache?> ObtenerPorHashAsync(string hashSha256, CancellationToken cancellationToken = default) =>
        Task.FromResult(_porHash.GetValueOrDefault(hashSha256));

    public void Agregar(ExtraccionIaCache cache) => _porHash[cache.HashSha256] = cache;
}
