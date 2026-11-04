using CaeManager.Application.Comercial.Common;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence;

/// <inheritdoc cref="IBloqueoSuscripcionStripe" />
/// <remarks>
/// Candado de asesoramiento de transacción, con la misma convención que
/// <see cref="BloqueoCarteraUsuario"/>: un espacio con nombre y <c>hashtextextended</c>. Una
/// colisión de hash solo serializa de más.
/// </remarks>
public class BloqueoSuscripcionStripe(CaeManagerDbContext dbContext) : IBloqueoSuscripcionStripe
{
    private const string EspacioTenant = "app.estado_comercial_tenant:";
    private const string EspacioSuscripcion = "app.suscripcion_stripe:";

    public Task BloquearTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        ExigirTransaccion();
        var clave = EspacioTenant + tenantId;
        return dbContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({clave}, 0))", cancellationToken);
    }

    public async Task BloquearSuscripcionesAsync(IReadOnlyCollection<string> idsSuscripcion, CancellationToken cancellationToken = default)
    {
        ExigirTransaccion();
        foreach (var id in idsSuscripcion.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var clave = EspacioSuscripcion + id;
            await dbContext.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({clave}, 0))", cancellationToken);
        }
    }

    public async Task<bool> IntentarBloquearSuscripcionAsync(string idSuscripcion, CancellationToken cancellationToken = default)
    {
        ExigirTransaccion();
        var clave = EspacioSuscripcion + idSuscripcion;
        return (await dbContext.Database.SqlQuery<bool>(
            $"SELECT pg_try_advisory_xact_lock(hashtextextended({clave}, 0)) AS \"Value\"").ToListAsync(cancellationToken)).Single();
    }

    private void ExigirTransaccion()
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "El candado de la Subscription de Stripe solo dura lo que su transacción: tómalo dentro de ITransaccionDeComando.");
    }
}
