using System.Runtime.CompilerServices;
using CaeManager.Application.Tenants.Encargo;
using MediatR;

namespace CaeManager.Application.Common;

/// <summary>
/// La misma restricción que <see cref="ExclusionesDelEncargoBehavior{TRequest,TResponse}"/> para las peticiones de
/// flujo de MediatR (<see cref="IStreamRequest{TResponse}"/>), que no pasan por los <see cref="IPipelineBehavior{TRequest,TResponse}"/>
/// sino por los <see cref="IStreamPipelineBehavior{TRequest,TResponse}"/> (Codex r2, #2241). Sin él, una petición de
/// flujo de un área excluida, o sin área, se ejecutaría bajo Encargo de administración no pleno sin barrera alguna.
///
/// <para>
/// Misma semántica: solo niega, y solo a quien tiene el rol elevado por un encargo no pleno; la petición sin área
/// resuelta queda excluida (<see cref="ActosExcluidosDelEncargo.Excluye"/> falla cerrado). Un flujo no devuelve
/// <c>Result</c>, así que la negación interrumpe con <see cref="ActoExcluidoDelEncargoException"/> antes de producir
/// el primer elemento y sin invocar el handler.
/// </para>
/// </summary>
public class ExclusionesDelEncargoStreamBehavior<TRequest, TResponse>(IEncargoDeAdministracionActual encargoActual)
    : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async IAsyncEnumerable<TResponse> Handle(
        TRequest request, StreamHandlerDelegate<TResponse> next, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (ActosExcluidosDelEncargo.Excluye(request)
            && await encargoActual.EncargoQueElevaAsync() is not null
            && await encargoActual.EncargoPlenoAsync() is null)
        {
            throw new ActoExcluidoDelEncargoException(request.GetType().Name);
        }

        await foreach (var elemento in next().WithCancellation(cancellationToken))
            yield return elemento;
    }
}
