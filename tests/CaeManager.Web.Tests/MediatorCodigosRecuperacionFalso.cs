using CaeManager.Application.Usuarios.Commands.GenerarCodigosRecuperacion;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Web.Tests;

/// <summary>
/// <c>IMediator</c> de las páginas de cuenta que envían
/// <see cref="GenerarCodigosRecuperacionCommand"/> (P0-8). Responde con
/// <see cref="Codigos"/> o, si se le pasa un error, con ese fallo; cualquier
/// otra petición es un error del test, no una respuesta vacía.
/// </summary>
internal sealed class MediatorCodigosRecuperacionFalso(Error? fallo = null) : IMediator
{
    public static readonly IReadOnlyList<string> Codigos =
        Enumerable.Range(0, CodigosRecuperacion.Cantidad).Select(i => $"AB{i:00}C-DE{i:00}F").ToList();

    public int Enviados { get; private set; }

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (request is not GenerarCodigosRecuperacionCommand)
            throw new NotSupportedException($"Petición no esperada en el test: {request.GetType().Name}");

        Enviados++;
        object respuesta = fallo is null
            ? Result.Exito(Codigos)
            : Result.Fallo<IReadOnlyList<string>>(fallo);
        return Task.FromResult((TResponse)respuesta);
    }

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
        throw new NotSupportedException();

    public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification => Task.CompletedTask;
}
