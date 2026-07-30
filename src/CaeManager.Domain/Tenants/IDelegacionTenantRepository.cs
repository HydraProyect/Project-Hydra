namespace CaeManager.Domain.Tenants;

public interface IDelegacionTenantRepository
{
    Task<DelegacionTenant?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExisteActivaAsync(Guid tenantConsultoraId, Guid tenantClienteId, CancellationToken cancellationToken = default);

    void Agregar(DelegacionTenant delegacion);
}
