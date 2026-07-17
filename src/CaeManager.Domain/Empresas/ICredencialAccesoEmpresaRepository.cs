namespace CaeManager.Domain.Empresas;

public interface ICredencialAccesoEmpresaRepository
{
    Task<CredencialAccesoEmpresa?> ObtenerPorEmpresaAsync(Guid empresaId, CancellationToken cancellationToken = default);

    void Agregar(CredencialAccesoEmpresa credencial);
}
