using MediatR;

namespace CaeManager.Application.Common;

/// <summary>
/// Hace pasar todo request de MediatR por <see cref="PuertaAccesoDatos"/>,
/// para que dos componentes de Blazor que se inicializan en paralelo no
/// ejecuten dos operaciones a la vez sobre el mismo DbContext scoped (ver el
/// comentario de la puerta para el porqué completo).
///
/// Va el primero del pipeline, por fuera incluso de
/// <c>ConcurrenciaBehavior</c>: la serialización tiene que abarcar el request
/// entero, incluidos los otros behaviors que también tocan datos
/// (<c>AutorizacionEscrituraBehavior</c> consulta el rol del usuario).
/// </summary>
public class SerializacionAccesoDatosBehavior<TRequest, TResponse>(PuertaAccesoDatos puerta)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        => puerta.EjecutarAsync(() => next(cancellationToken), cancellationToken);
}
