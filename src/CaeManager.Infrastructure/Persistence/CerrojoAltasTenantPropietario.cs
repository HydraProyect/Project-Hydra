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

        // Espera acotada por intento: mientras el LOCK espera, PostgreSQL pone detrás de él cualquier escritura nueva en
        // Tenants. Pasados 5 s responde 55P03, que la estrategia de reintentos trata como transitorio: deshace el intento,
        // suelta la cola y repite la transacción entera, sin guardar nada. El plazo sigue el resto de la transacción.
        // La sentencia va en la misma línea que la llamada para que el ratchet de SQL crudo la congele entera.
        return dbContext.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'; LOCK TABLE public.\"Tenants\" IN SHARE MODE", cancellationToken);
    }
}
