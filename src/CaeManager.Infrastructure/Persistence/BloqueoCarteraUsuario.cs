using CaeManager.Application.Clientes;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence;

/// <inheritdoc cref="IBloqueoCarteraUsuario" />
/// <remarks>
/// Candado de asesoramiento de transacción, no de fila: no depende de que la RLS deje ver
/// la fila de <c>AspNetUsers</c> (la de un Operador CAE externo no pasa la política de
/// escritura del Tenant propietario) ni exige privilegio de UPDATE. La clave sigue la
/// convención de <c>SerializarAdministradorUnicoEnRestablecimiento</c>: un espacio con nombre
/// y <c>hashtextextended</c>. Los compartidos se toman en orden de clave para que dos
/// reasignaciones no se crucen; una colisión de hash solo serializa de más.
/// </remarks>
public class BloqueoCarteraUsuario(CaeManagerDbContext dbContext) : IBloqueoCarteraUsuario
{
    private const string Espacio = "app.cartera_de_usuario:";

    public Task BloquearExclusivoAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        ExigirTransaccion();
        var clave = Espacio + usuarioId;
        return dbContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({clave}, 0))", cancellationToken);
    }

    public async Task BloquearCompartidoAsync(IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken = default)
    {
        ExigirTransaccion();
        foreach (var usuarioId in usuarioIds.Distinct().Order())
        {
            var clave = Espacio + usuarioId;
            await dbContext.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock_shared(hashtextextended({clave}, 0))", cancellationToken);
        }
    }

    private void ExigirTransaccion()
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "El candado de cartera solo dura lo que su transacción: tómalo dentro de ITransaccionDeComando.");
    }
}
