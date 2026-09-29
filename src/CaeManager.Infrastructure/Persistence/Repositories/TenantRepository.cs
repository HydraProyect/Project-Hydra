using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class TenantRepository(CaeManagerDbContext dbContext) : ITenantRepository
{
    public Task<bool> ExisteConNombreAsync(string nombre, CancellationToken cancellationToken = default) =>
        dbContext.Tenants.AnyAsync(t => t.Nombre == nombre, cancellationToken);

    public Task<Tenant?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public void Agregar(Tenant tenant) => dbContext.Tenants.Add(tenant);
}
