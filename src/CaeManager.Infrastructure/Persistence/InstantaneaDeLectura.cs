using System.Data;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence;

/// <summary>
/// Una transacción de solo lectura REPEATABLE READ alrededor de una lectura de varias consultas: todas ven la misma foto
/// de la base, fijada por la primera, y lo que otra conexión confirme después no se ve. Mismo patrón que el cotejo
/// nocturno del estado materializado (<c>CambioDeDiaEstadoMaterializado</c>): la transacción va dentro de
/// <c>CreateExecutionStrategy()</c>, porque <c>NpgsqlRetryingExecutionStrategy</c> no admite una que el código abra por su
/// cuenta; se declara de solo lectura y la base lo hace cumplir; y se confirma al terminar (no hay nada que deshacer).
/// Si el contexto ya tiene una transacción (ambiente) no se abre otra: la lectura se ejecuta dentro de ella y comparte su
/// nivel de aislamiento.
/// </summary>
internal static class InstantaneaDeLectura
{
    public static Task<T> EjecutarAsync<T>(
        DbContext dbContext, Func<CancellationToken, Task<T>> lectura, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
            return lectura(cancellationToken);

        var estrategia = dbContext.Database.CreateExecutionStrategy();
        return estrategia.ExecuteAsync(async ct =>
        {
            await using var transaccion = await dbContext.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await dbContext.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
            var resultado = await lectura(ct);
            await transaccion.CommitAsync(ct);
            return resultado;
        }, cancellationToken);
    }
}
