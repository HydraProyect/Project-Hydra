using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Tests.Integraciones;

public class ProveedorPlataformaCaeRepositorioFalso : IProveedorPlataformaCaeRepository
{
    public List<ProveedorPlataformaCae> Proveedores { get; } = [];
    public List<DominioProveedorPlataformaCae> Dominios { get; } = [];

    public void Agregar(ProveedorPlataformaCae proveedor) => Proveedores.Add(proveedor);

    public void AgregarDominio(DominioProveedorPlataformaCae dominio) => Dominios.Add(dominio);

    public Task<ProveedorPlataformaCae?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Proveedores.FirstOrDefault(p => p.Id == id));
}
