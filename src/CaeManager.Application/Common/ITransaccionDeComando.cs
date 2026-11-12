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
/// Cada intento empieza con el alcance de datos sin memoizar (<see cref="IInvalidadorAlcance"/>):
/// lo que la operación autorice con <see cref="IAlcanceDatosService"/> se resuelve de nuevo
/// en el reintento, y una Asignación de Cartera revocada entre intentos ya no vale.
///
/// Si ya hay una transacción abierta en el contexto, la operación se une a ella y no se
/// confirma ni se deshace aquí: lo decide quien la abrió, que tiene que deshacer cuando
/// la operación de dentro devuelva un fallo.
/// </summary>
public interface ITransaccionDeComando
{
    Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default);
}
