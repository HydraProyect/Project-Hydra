using MediatR;

namespace CaeManager.Application.Common;

/// <summary>
/// Tras cada Command (<see cref="ICommandBase"/>) descarta el alcance memoizado del scope actual
/// (<see cref="IInvalidadorAlcance"/>), para que la siguiente lectura en el mismo circuito vea el
/// alcance ya actualizado. Las Queries no lo tocan.
///
/// Va justo dentro de <see cref="SerializacionAccesoDatosBehavior{TRequest,TResponse}"/>: la
/// invalidación ocurre mientras la puerta de acceso a datos sigue tomada, así que ninguna lectura
/// concurrente puede volver a memoizar el alcance viejo entre el guardado y la invalidación.
/// Invalida también si el handler falla: un guardado parcial o un fallo de concurrencia tampoco
/// deben dejar una visión que ya no se sabe si es cierta.
///
/// Límite conocido: solo cubre escrituras que pasan por MediatR. Una escritura directa sobre el
/// DbContext desde un componente, o un seeder, no invalida.
/// </summary>
public class InvalidacionAlcanceBehavior<TRequest, TResponse>(IInvalidadorAlcance invalidador)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ICommandBase) return await next(cancellationToken);

        try
        {
            return await next(cancellationToken);
        }
        finally
        {
            invalidador.Invalidar();
        }
    }
}
