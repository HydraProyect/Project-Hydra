using FluentValidation;
using MediatR;

namespace CaeManager.Application.Common;

/// <summary>
/// Pipeline behavior de MediatR: ejecuta todos los FluentValidation
/// validators registrados para el Command/Query antes del handler. Si hay
/// errores, lanza ValidationException — un middleware/behavior de más alto
/// nivel en Web la traduce a microcopy en español (ver UX_PATTERNS.md).
/// </summary>
public class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validadores)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!validadores.Any())
            return await next(cancellationToken);

        var contexto = new ValidationContext<TRequest>(request);

        var errores = (await Task.WhenAll(validadores.Select(v => v.ValidateAsync(contexto, cancellationToken))))
            .SelectMany(resultado => resultado.Errors)
            .Where(error => error is not null)
            .ToList();

        if (errores.Count > 0)
            throw new ValidationException(errores);

        return await next(cancellationToken);
    }
}
