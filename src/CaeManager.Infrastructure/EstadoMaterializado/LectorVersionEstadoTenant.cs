using CaeManager.Application.EstadoMaterializado.Frescura;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.EstadoMaterializado;

/// <inheritdoc cref="ILectorVersionEstadoTenant"/>
/// <remarks>
/// Sin filtro por <c>TenantId</c> propio: decide la conexión. La política RLS forzada de <c>VersionesEstadoTenant</c> solo
/// deja ver la fila de <c>app.tenant_id</c> (índice único por Tenant), y el filtro global de EF hace lo mismo. Se leen dos
/// filas como mucho para distinguir «una» de «varias» (una identidad sin RLS, como la de bootstrap), que no se cachea.
/// </remarks>
public sealed class LectorVersionEstadoTenant(CaeManagerDbContext dbContext) : ILectorVersionEstadoTenant
{
    public async Task<VersionEstadoLeida?> LeerAsync(CancellationToken cancellationToken)
    {
        var filas = await dbContext.VersionesEstadoTenant.AsNoTracking()
            .Select(v => new { v.TenantId, v.Version })
            .Take(2)
            .ToListAsync(cancellationToken);
        return filas.Count == 1 ? new VersionEstadoLeida(filas[0].TenantId, filas[0].Version) : null;
    }
}
