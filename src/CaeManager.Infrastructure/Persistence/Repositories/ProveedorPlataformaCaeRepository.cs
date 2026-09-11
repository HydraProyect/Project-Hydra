using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ProveedorPlataformaCaeRepository(CaeManagerDbContext dbContext) : IProveedorPlataformaCaeRepository
{
    public void Agregar(ProveedorPlataformaCae proveedor) => dbContext.ProveedoresPlataformaCae.Add(proveedor);

    public void AgregarDominio(DominioProveedorPlataformaCae dominio) => dbContext.DominiosProveedorPlataformaCae.Add(dominio);

    public Task<ProveedorPlataformaCae?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ProveedoresPlataformaCae.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
}
