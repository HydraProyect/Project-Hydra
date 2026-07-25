namespace CaeManager.Domain.DocumentosIa;

public interface IExtraccionIaCacheRepository
{
    Task<ExtraccionIaCache?> ObtenerPorHashAsync(string hashSha256, CancellationToken cancellationToken = default);

    void Agregar(ExtraccionIaCache cache);
}
