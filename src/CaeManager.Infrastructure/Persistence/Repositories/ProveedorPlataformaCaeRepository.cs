using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ProveedorPlataformaCaeRepository(CaeManagerDbContext dbContext) : IProveedorPlataformaCaeRepository
{
    // Proyecta: con una entidad ya seguida en el circuito, EF devolvería los valores viejos y el kill
    // switch no cortaría hasta cerrar ese circuito.
    public Task<InterruptoresConectorProveedor?> ObtenerInterruptoresConectorAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ProveedoresPlataformaCae
            .Where(p => p.Id == id)
            .Select(p => new InterruptoresConectorProveedor(p.Codigo, p.Activo, p.ConectorApiActivo))
            .SingleOrDefaultAsync(cancellationToken);

    public void Agregar(ProveedorPlataformaCae proveedor) => dbContext.ProveedoresPlataformaCae.Add(proveedor);

    public void AgregarDominio(DominioProveedorPlataformaCae dominio) => dbContext.DominiosProveedorPlataformaCae.Add(dominio);

    public Task<ProveedorPlataformaCae?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ProveedoresPlataformaCae.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
}
