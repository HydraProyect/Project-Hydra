using CaeManager.Domain.Empresas;

namespace CaeManager.Application.Tests.Documentos;

public class EmpresaRepositorioFalso : IEmpresaRepository
{
    public List<Empresa> Empresas { get; } = [];

    public Task<Empresa?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Empresas.FirstOrDefault(e => e.Id == id));

    public Task<bool> ExisteConRazonSocialAsync(string razonSocial, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Empresas.Any(e => e.RazonSocial == razonSocial && e.Id != excluirId));

    public Task<bool> ExisteConCifAsync(string cif, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Empresas.Any(e => e.Cif == cif && e.Id != excluirId));

    public Task<bool> TieneTrabajadoresAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<bool> TieneCentrosComoTitularAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<bool> TieneTrabajadoresComoSubcontrataAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public void Agregar(Empresa empresa) => Empresas.Add(empresa);
}
