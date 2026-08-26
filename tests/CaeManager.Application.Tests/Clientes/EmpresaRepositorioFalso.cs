using CaeManager.Domain.Empresas;

namespace CaeManager.Application.Tests.Clientes;

/// <summary>
/// Fake en memoria — los handlers de Application se prueban sin base de
/// datos (ver CODING_STANDARDS.md). F3b: reemplaza a
/// <c>ClienteRepositorioFalso</c> — desde la congelación, "Cliente" es
/// Empresa contraparte (<see cref="Empresa.CrearComoCliente"/>).
/// </summary>
public class EmpresaRepositorioFalso : IEmpresaRepository
{
    public List<Empresa> Empresas { get; } = [];
    public bool TieneCentrosActivos { get; set; }

    /// <summary>Control fino por id, para probar éxito parcial en borrado en lote (P3-31) sin afectar <see cref="TieneCentrosActivos"/>.</summary>
    public HashSet<Guid> IdsConCentrosActivos { get; } = [];

    public Task<Empresa?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Empresas.FirstOrDefault(e => e.Id == id));

    public Task<bool> ExisteConRazonSocialAsync(string razonSocial, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Empresas.Any(e => e.RazonSocial == razonSocial && e.Id != excluirId));

    public Task<bool> ExisteConCifAsync(string cif, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Empresas.Any(e => e.Cif == cif && e.Id != excluirId));

    public Task<bool> TieneTrabajadoresAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<bool> TieneCentrosComoTitularAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
        Task.FromResult(TieneCentrosActivos || IdsConCentrosActivos.Contains(empresaId));

    public void Agregar(Empresa empresa) => Empresas.Add(empresa);
}
