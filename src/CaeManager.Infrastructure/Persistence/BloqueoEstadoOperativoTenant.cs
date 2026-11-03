using CaeManager.Application.Tenants;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence;

/// <inheritdoc cref="IBloqueoEstadoOperativoTenant" />
/// <remarks>Misma convención que <see cref="BloqueoCarteraUsuario"/>: candado de asesoramiento de transacción con espacio propio.</remarks>
public class BloqueoEstadoOperativoTenant(CaeManagerDbContext dbContext) : IBloqueoEstadoOperativoTenant
{
    private const string Espacio = "app.estado_operativo_tenant:";

    public Task BloquearAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("El candado del estado operativo del Tenant exige una transacción en curso.");

        var clave = Espacio + tenantId;
        return dbContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({clave}, 0))", cancellationToken);
    }
}
