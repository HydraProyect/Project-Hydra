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

        // Espera acotada: mientras el LOCK espera, PostgreSQL pone detrás de él cualquier escritura nueva en Tenants. Pasado
        // el plazo falla con 55P03 y no se guarda nada; el plazo sigue para el resto de la transacción, que es corta.
        return dbContext.Database.ExecuteSqlRawAsync(
            "SET LOCAL lock_timeout = '5s'; LOCK TABLE public.\"Tenants\" IN SHARE MODE", cancellationToken);
    }
}
