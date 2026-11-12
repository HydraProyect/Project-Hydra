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
/// Cada intento empieza también con el alcance de datos sin memoizar (<see cref="IInvalidadorAlcance"/>):
/// <c>InvalidacionAlcanceBehavior</c> lo descarta antes del Command, pero eso ocurre una sola vez, fuera de la
/// estrategia de reintentos. Un cuerpo que autoriza con <see cref="IAlcanceDatosService"/> y cuyo primer intento
/// falla de forma transitoria volvería a autorizar, en el reintento, con la lista memoizada en el primero: una
/// Asignación de Cartera revocada entre los dos intentos seguiría valiendo (revisión Codex r4 de la PR #2172).
/// En el primer intento de un Command es redundante con el behavior y no cambia nada; en una Query solo obliga a
/// resolver otra vez lo que se pida después.
///
/// Es también la envoltura del estado materializado (diseño § 4.3): antes del COMMIT recalcula lo que el detector recogió
/// de los guardados de la operación, dentro de un SAVEPOINT (<see cref="DetectorCambiosEstadoInterceptor"/>). Un fallo de
/// ese recálculo no hace fallar la operación: va a la cola.
/// </remarks>
public class TransaccionDeComando(CaeManagerDbContext dbContext, IInvalidadorAlcance? invalidadorAlcance = null)
    : ITransaccionDeComando
{
    public Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default)
    {
        // Reentrante: un Command que abre su transacción y se ejecuta dentro de otra (el paso
        // de un plan de Vega envía CrearClienteCommand dentro de la suya) se une a la de
        // fuera. Confirmar o deshacer es de quien la abrió: si el Command de dentro falla,
        // devuelve el fallo y quien está fuera tiene que deshacer, como hace
        // EjecutarPlanTareaAsistenteCommandHandler devolviendo ese fallo.
        if (dbContext.Database.CurrentTransaction is not null)
            return operacion(cancellationToken);

        var estrategia = dbContext.Database.CreateExecutionStrategy();

        return estrategia.ExecuteAsync(async ct =>
        {
            invalidadorAlcance?.Invalidar();
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
