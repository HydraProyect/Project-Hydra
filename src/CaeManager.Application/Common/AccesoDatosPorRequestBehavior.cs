using MediatR;

namespace CaeManager.Application.Common;

/// <summary>
/// Hace pasar todo request de MediatR por <see cref="IPuertaAccesoDatos"/>,
/// para que cada Command/Query reciba su propio <c>CaeManagerDbContext</c> en
/// vez de compartir el de otro request que se esté ejecutando en paralelo
/// dentro del mismo circuito de Blazor (ver el comentario de
/// <c>PuertaAccesoDatos</c>, en Infrastructure, para el porqué completo).
///
/// Va segundo del pipeline, justo detrás de <c>LoggingBehavior</c> —que no
/// toca la base de datos— y por fuera de todos los demás: la creación del
/// contexto tiene que abarcar el resto del request entero, incluidos los
/// otros behaviors que también tocan datos (<c>AutorizacionEscrituraBehavior</c>
/// consulta el rol del usuario, que puede requerir una consulta si hay un
/// Delegated Workspace en juego).
/// </summary>
public class AccesoDatosPorRequestBehavior<TRequest, TResponse>(IPuertaAccesoDatos puerta)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        => puerta.EjecutarAsync(() => next(cancellationToken), cancellationToken);
}
