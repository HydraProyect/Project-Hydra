using CaeManager.Domain.Common;

namespace CaeManager.Application.Common;

/// <summary>
/// Ejecuta una operación de varios guardados como una sola transacción de base de datos.
///
/// Existe para los Commands que escriben con más de un <c>SaveChangesAsync</c> —o con
/// uno propio y otro que hace Identity por dentro— y no pueden quedarse a medias: si
/// la operación devuelve un <see cref="Result"/> fallido o lanza, no queda nada escrito
/// y el contexto de persistencia se vacía, para que el siguiente Command del mismo
/// circuito no guarde los restos (ver <see cref="IDescarteCambiosPendientes"/>).
///
/// La implementación va envuelta en la estrategia de ejecución del contexto: con
/// reintentos activados, una transacción abierta a mano sin ella se rechaza. Por eso la
/// operación puede ejecutarse más de una vez y tiene que leer lo que necesite dentro.
/// </summary>
public interface ITransaccionDeComando
{
    Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default);
}
