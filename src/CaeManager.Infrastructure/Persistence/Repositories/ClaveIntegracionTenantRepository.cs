using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ClaveIntegracionTenantRepository(CaeManagerDbContext dbContext) : IClaveIntegracionTenantRepository
{
    /// <summary>Mismo criterio que <see cref="ClaveApiRepository.ObtenerTenantPorHashAsync"/>.</summary>
    public async Task<Guid?> ObtenerTenantPorHashAsync(string hashClave, CancellationToken cancellationToken = default) =>
        await dbContext.Database
            .SqlQuery<Guid?>($"SELECT app_tenant_de_clave_integracion({hashClave}) AS \"Value\"")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<ClaveIntegracionTenant?> ObtenerPorHashAsync(string hashClave, CancellationToken cancellationToken = default) =>
        dbContext.ClavesIntegracionTenant.FirstOrDefaultAsync(c => c.HashClave == hashClave, cancellationToken);

    public Task<ClaveIntegracionTenant?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ClavesIntegracionTenant.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<bool> TenantAdmiteClaveAsync(Guid tenantPropietarioId, DateTime emitidaEnUtc, CancellationToken cancellationToken = default)
    {
        var tenant = await dbContext.Tenants
            .Where(t => t.Id == tenantPropietarioId)
            .Select(t => new { t.Estado, t.EstadoComercial, t.ClavesApiValidasDesdeUtc, t.IntegracionHabilitada })
            .SingleOrDefaultAsync(cancellationToken);

        return tenant is not null
            && tenant.IntegracionHabilitada
            && Tenant.AdmiteClaveEmitidaEn(tenant.Estado, tenant.EstadoComercial, tenant.ClavesApiValidasDesdeUtc, emitidaEnUtc);
    }
}
