using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using MediatR;

namespace CaeManager.IntegrationTests.BusquedaGlobal;

/// <summary>
/// Mediador de las pruebas de alcance de la búsqueda global: el estado documental del Trabajador encontrado por
/// DNI/NIE (ObtenerTrabajadoresQuery) vuelve vacío, así que el Trabajador sale sin estado. Estas pruebas miran qué
/// se encuentra y qué no se proyecta; el estado lo cubren las pruebas de Application de la paleta.
/// </summary>
internal sealed class MediadorSinEstadoDocumental : ISender
{
    public List<object> Enviados { get; } = [];

    public Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default)
    {
        Enviados.Add(request);
        return request is ObtenerTrabajadoresQuery
            ? Task.FromResult((T)(object)new ResultadoPaginado<TrabajadorListaDto>([], 0, 1, 1))
            : throw new NotSupportedException(request.GetType().Name);
    }

    public Task Send<T>(T request, CancellationToken cancellationToken = default) where T : IRequest => throw new NotSupportedException();
    public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}