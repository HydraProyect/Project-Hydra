using MediatR;

namespace CaeManager.Application.Common;

/// <summary>
/// Cuenta cada despacho de MediatR en el <see cref="CosteCarga"/> del scope
/// (métrica <c>med</c> de <c>Server-Timing</c>). El más externo del pipeline:
/// no toca la base de datos, no espera la puerta y no altera el resultado ni
/// las excepciones. Cuenta también los despachos anidados (un handler que
/// despacha otro request), porque cada uno recorre su propio pipeline.
/// </summary>
public sealed class CosteCargaBehavior<TRequest, TResponse>(CosteCarga coste)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        coste.RegistrarDespacho();
        return next(cancellationToken);
    }
}
