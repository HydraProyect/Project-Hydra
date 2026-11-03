namespace CaeManager.Application.Tenants;

/// <summary>
/// Serializa las transiciones de <see cref="CaeManager.Domain.Tenants.EstadoTenant"/> de un Tenant.
/// Sin él, una reactivación que tardara en guardar podría pisar una suspensión posterior y dejar
/// con ello el sello de las claves de API en un instante anterior. El candado dura lo que la
/// transacción que lo toma, así que exige una en curso.
/// </summary>
public interface IBloqueoEstadoOperativoTenant
{
    Task BloquearAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
