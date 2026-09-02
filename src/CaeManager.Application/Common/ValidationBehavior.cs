using FluentValidation;
using MediatR;

namespace CaeManager.Application.Common;

/// <summary>
/// Pipeline behavior de MediatR: ejecuta todos los FluentValidation
/// validators registrados para el Command/Query antes del handler. Si hay
/// errores, lanza ValidationException — un middleware/behavior de más alto
/// nivel en Web la traduce a microcopy en español (ver UX_PATTERNS.md).
///
/// Es el último behavior antes del handler (ver el orden en
/// ApplicationServiceCollectionExtensions), así que <c>next(cancellationToken)</c>
/// es lo más cerca que este pipeline llega de invocar directamente el
/// handler y de que MediatR resuelva lo que le falte del contenedor. Por eso
/// es aquí, y no en un behavior más externo, donde se distingue la carrera
/// documentada en <see cref="EjecutarSiElScopeSigueVivoAsync"/>.
/// </summary>
public class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validadores)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!validadores.Any())
            return await EjecutarSiElScopeSigueVivoAsync(next, cancellationToken);

        var contexto = new ValidationContext<TRequest>(request);

        var errores = (await Task.WhenAll(validadores.Select(v => v.ValidateAsync(contexto, cancellationToken))))
            .SelectMany(resultado => resultado.Errors)
            .Where(error => error is not null)
            .ToList();

        if (errores.Count > 0)
            throw new ValidationException(errores);

        return await EjecutarSiElScopeSigueVivoAsync(next, cancellationToken);
    }

    /// <summary>
    /// Sentry DOTNET-4, en producción: el circuito de Blazor se desconecta
    /// (y con él, el scope de DI de la petición) mientras un componente
    /// todavía está mid-flight despachando un <c>Send</c> de MediatR — ver
    /// <c>SelectorClienteActivo.OnInitializedAsync</c> en el evento real. La
    /// resolución de lo que sigue del pipeline revienta contra un
    /// <c>IServiceProvider</c> ya disposed, y sin distinguir esta carrera de
    /// un fallo real, el resultado es un <c>ObjectDisposedException</c> sin
    /// observar que el finalizador relanza más tarde (mecanismo
    /// <c>UnobservedTaskException</c> en el evento de Sentry) — ruido que no
    /// tiene componente al otro lado que lo necesite.
    ///
    /// Se convierte en <see cref="OperationCanceledException"/> — no se
    /// silencia del todo — porque <c>LoggingBehavior</c> (fuera de este
    /// pipeline, no es de esta sesión) YA trata esa excepción como lo que
    /// es: un circuito que se fue, registrado a Debug y sin contar como
    /// fallo en <c>VentanaSaludOperativa</c>, en vez de un error real
    /// registrado y alertado.
    ///
    /// Acotado a propósito al nombre exacto del objeto disposed
    /// (<c>IServiceProvider</c>, el mismo que aparece en el evento real):
    /// un <c>ObjectDisposedException</c> de cualquier otro objeto (un
    /// <c>Stream</c>, un <c>HttpClient</c> liberado por error dentro de un
    /// handler) sigue siendo un fallo real y no se toca.
    /// </summary>
    private static async Task<TResponse> EjecutarSiElScopeSigueVivoAsync(
        RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        try
        {
            return await next(cancellationToken);
        }
        catch (ObjectDisposedException ex) when (ex.ObjectName == nameof(IServiceProvider))
        {
            throw new OperationCanceledException(
                "El scope de DI se liberó mientras el pipeline de MediatR seguía en vuelo.", ex, cancellationToken);
        }
    }
}
