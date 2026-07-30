using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class DelegacionTenantRepository(CaeManagerDbContext dbContext) : IDelegacionTenantRepository
{
    public Task<DelegacionTenant?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.DelegacionesTenant.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    public Task<bool> ExisteActivaAsync(Guid tenantConsultoraId, Guid tenantClienteId, CancellationToken cancellationToken = default) =>
        dbContext.DelegacionesTenant.AnyAsync(
            d => d.TenantConsultoraId == tenantConsultoraId && d.TenantClienteId == tenantClienteId && d.Activa,
            cancellationToken);

    public void Agregar(DelegacionTenant delegacion) => dbContext.DelegacionesTenant.Add(delegacion);
}
