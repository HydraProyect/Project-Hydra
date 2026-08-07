using CaeManager.Domain.Integraciones;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ProveedorPlataformaCaeRepository(CaeManagerDbContext dbContext) : IProveedorPlataformaCaeRepository
{
    public void Agregar(ProveedorPlataformaCae proveedor) => dbContext.ProveedoresPlataformaCae.Add(proveedor);

    public void AgregarDominio(DominioProveedorPlataformaCae dominio) => dbContext.DominiosProveedorPlataformaCae.Add(dominio);
}
