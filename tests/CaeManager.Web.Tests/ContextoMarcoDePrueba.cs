using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CaeManager.Application.Marco.Queries.ObtenerContextoMarco;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using MediatR;

namespace CaeManager.Web.Tests;

/// <summary>
/// El contexto del marco (<see cref="ObtenerContextoMarcoQuery"/>) en los tests de los componentes del marco: se compone
/// con el handler real sobre las respuestas que el test ya da a cada Query, así que el test sigue diciendo qué devuelve
/// cada una y lo que se prueba es el mismo contenido que antes. Una Query del contexto que el test no responde
/// (<see cref="NotSupportedException"/> o <c>null</c>) sale con su valor neutro: perfil por defecto, falso, lista vacía
/// o sin avatar. Cualquier otra excepción se propaga, como en la composición real.
/// </summary>
internal static class ContextoMarcoDePrueba
{
    public static Task<ContextoMarcoDto> ComponerAsync(ISender mediador, CancellationToken cancellationToken = default) =>
        new ObtenerContextoMarcoQueryHandler(new Relleno(mediador)).Handle(new ObtenerContextoMarcoQuery(), cancellationToken);

    public static Task<ContextoMarcoDto> ComponerAsync(Func<object, object?> responder, CancellationToken cancellationToken = default) =>
        ComponerAsync(new Respondedor(responder), cancellationToken);

    private static T Neutro<T>(object request) =>
        request is ObtenerClientesAutorizadosQuery ? (T)(object)Array.Empty<ClienteAutorizadoDto>() : default!;

    private sealed class Relleno(ISender interno) : ISender
    {
        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            try
            {
                var respuesta = await interno.Send(request, cancellationToken);
                return respuesta is null ? Neutro<TResponse>(request) : respuesta;
            }
            catch (NotSupportedException)
            {
                return Neutro<TResponse>(request);
            }
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class Respondedor(Func<object, object?> responder) : ISender
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult(responder(request) is TResponse respuesta ? respuesta : default!);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
