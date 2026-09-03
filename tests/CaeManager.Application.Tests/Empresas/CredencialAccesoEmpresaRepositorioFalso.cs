using CaeManager.Domain.Empresas;

namespace CaeManager.Application.Tests.Empresas;

public class CredencialAccesoEmpresaRepositorioFalso : ICredencialAccesoEmpresaRepository
{
    public List<CredencialAccesoEmpresa> Credenciales { get; } = [];

    public Task<CredencialAccesoEmpresa?> ObtenerPorEmpresaAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Credenciales.FirstOrDefault(c => c.EmpresaId == empresaId));

    public void Agregar(CredencialAccesoEmpresa credencial) => Credenciales.Add(credencial);
}
