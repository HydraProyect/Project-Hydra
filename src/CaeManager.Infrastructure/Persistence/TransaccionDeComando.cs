using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.EstadoMaterializado;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence;

/// <inheritdoc cref="ITransaccionDeComando" />
/// <remarks>
/// Mismo patrón que <c>TrabajoAnalisisDocumentoRepository.ReclamarSiguientePendienteAsync</c>:
/// la transacción explícita va dentro de <c>CreateExecutionStrategy()</c>, porque
/// <c>NpgsqlRetryingExecutionStrategy</c> no admite una que el código abra por su cuenta.
/// Cada intento empieza con el contexto sin cambios pendientes: un reintento no puede
/// arrastrar lo que el intento anterior dejó añadido o modificado.
///
/// Es también la envoltura del estado materializado (diseño § 4.3): antes del COMMIT recalcula lo que el detector recogió
/// de los guardados de la operación, dentro de un SAVEPOINT (<see cref="DetectorCambiosEstadoInterceptor"/>). Un fallo de
/// ese recálculo no hace fallar la operación: va a la cola.
/// </remarks>
public class TransaccionDeComando(CaeManagerDbContext dbContext) : ITransaccionDeComando
{
    public Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default)
    {
        var estrategia = dbContext.Database.CreateExecutionStrategy();

        return estrategia.ExecuteAsync(async ct =>
        {
            await using var transaccion = await dbContext.Database.BeginTransactionAsync(ct);
            try
            {
                var resultado = await operacion(ct);
                if (resultado.EsFallido)
                {
                    await transaccion.RollbackAsync(ct);
                    dbContext.ChangeTracker.Clear();
                    DetectorCambiosEstadoInterceptor.De(dbContext)?.Descartar(dbContext);
                    return resultado;
                }

                if (DetectorCambiosEstadoInterceptor.De(dbContext) is { } detector)
                    await detector.ProcesarAntesDeConfirmarAsync(dbContext);
                await transaccion.CommitAsync(ct);
                return resultado;
            }
            catch
            {
                // Sin RollbackAsync explícito: el DisposeAsync de la transacción ya la
                // deshace, y una conexión rota lanzaría aquí tapando la excepción real.
                dbContext.ChangeTracker.Clear();
                DetectorCambiosEstadoInterceptor.De(dbContext)?.Descartar(dbContext);
                throw;
            }
        }, cancellationToken);
    }
}
