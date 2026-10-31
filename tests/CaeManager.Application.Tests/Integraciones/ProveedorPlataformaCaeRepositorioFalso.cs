using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Tests.Integraciones;

public class ProveedorPlataformaCaeRepositorioFalso : IProveedorPlataformaCaeRepository
{
    public List<ProveedorPlataformaCae> Proveedores { get; } = [];
    public List<DominioProveedorPlataformaCae> Dominios { get; } = [];

    public Task<InterruptoresConectorProveedor?> ObtenerInterruptoresConectorAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Proveedores.Where(p => p.Id == id)
            .Select(p => new InterruptoresConectorProveedor(p.Codigo, p.Activo, p.ConectorApiActivo)).FirstOrDefault());

    public void Agregar(ProveedorPlataformaCae proveedor) => Proveedores.Add(proveedor);

    public void AgregarDominio(DominioProveedorPlataformaCae dominio) => Dominios.Add(dominio);

    public Task<ProveedorPlataformaCae?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Proveedores.FirstOrDefault(p => p.Id == id));
}
