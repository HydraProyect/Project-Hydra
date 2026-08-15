using MediatR;

namespace CaeManager.Application.Tests;

/// <summary>
/// <see cref="IMediator"/> mínimo para handlers que envían un Command
/// anidado (p. ej. FirmarDocumentoEnCampoCommandHandler guardando la firma
/// habitual) — registra lo enviado y devuelve <see cref="Respuesta"/> para
/// cualquier Send, sin despachar a un handler real.
/// </summary>
public class MediatorFalso : IMediator
{
    public List<object> Enviados { get; } = [];
    public object? Respuesta { get; set; }

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        Enviados.Add(request);
        return Task.FromResult((TResponse)Respuesta!);
    }

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
    {
        Enviados.Add(request!);
        return Task.CompletedTask;
    }

    public Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        Enviados.Add(request);
        return Task.FromResult(Respuesta);
    }

    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("MediatorFalso no soporta streams.");

    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("MediatorFalso no soporta streams.");

    public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification => Task.CompletedTask;
}
