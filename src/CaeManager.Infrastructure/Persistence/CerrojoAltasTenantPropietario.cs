using CaeManager.Application.Cumplimiento.Subencargados;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence;

/// <inheritdoc cref="ICerrojoAltasTenantPropietario" />
public class CerrojoAltasTenantPropietario(CaeManagerDbContext dbContext) : ICerrojoAltasTenantPropietario
{
    public Task CerrarAsync(CancellationToken cancellationToken = default)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("El cerrojo de las altas de Tenant propietario exige una transacción en curso.");

        return dbContext.Database.ExecuteSqlRawAsync("LOCK TABLE public.\"Tenants\" IN SHARE MODE", cancellationToken);
    }
}
