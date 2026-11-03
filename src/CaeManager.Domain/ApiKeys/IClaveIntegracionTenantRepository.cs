namespace CaeManager.Domain.ApiKeys;

public interface IClaveIntegracionTenantRepository
{
    /// <summary>
    /// De quién es la clave, antes de tener Tenant: la única lectura que no pasa por RLS, por la función
    /// <c>app_tenant_de_clave_integracion</c>. Solo devuelve el Tenant, nunca la fila.
    /// </summary>
    Task<Guid?> ObtenerTenantPorHashAsync(string hashClave, CancellationToken cancellationToken = default);

    /// <summary>Exige estar ya en el ámbito del Tenant propietario de la clave.</summary>
    Task<ClaveIntegracionTenant?> ObtenerPorHashAsync(string hashClave, CancellationToken cancellationToken = default);

    /// <summary>Exige estar ya en el ámbito del Tenant propietario de la clave.</summary>
    Task<ClaveIntegracionTenant?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Los cortes que no están en la fila de la clave: el Tenant propietario la admite
    /// (<c>Tenant.AdmiteClaveEmitidaEn</c>) y tiene la integración habilitada. Sin la fila del Tenant, no.
    /// </summary>
    Task<bool> TenantAdmiteClaveAsync(Guid tenantPropietarioId, DateTime emitidaEnUtc, CancellationToken cancellationToken = default);
}
