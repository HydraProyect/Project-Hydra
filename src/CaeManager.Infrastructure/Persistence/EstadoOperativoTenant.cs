using CaeManager.Application.Tenants;
using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence;

/// <inheritdoc cref="IEstadoOperativoTenant" />
/// <remarks>Proyección y no entidad: el contexto de un circuito conservaría el estado de una fila ya seguida.</remarks>
public class EstadoOperativoTenant(ITenantsQueryContext dbContext) : IEstadoOperativoTenant
{
    public Task<bool> EstaSuspendidoAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        dbContext.Tenants.AnyAsync(t => t.Id == tenantId && t.Estado == EstadoTenant.Suspendido, cancellationToken);
}
