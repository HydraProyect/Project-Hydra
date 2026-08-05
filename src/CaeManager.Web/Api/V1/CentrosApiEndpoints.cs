using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using MediatR;

namespace CaeManager.Web.Api.V1;

public static class CentrosApiEndpoints
{
    public static IEndpointRouteBuilder MapCentrosApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // pagina/tamanoPagina llevan valor por defecto: sin él, Minimal API los
        // trata como parámetros de query obligatorios (verificado con curl).
        endpoints.MapGet("/centros", async (
            string? busqueda, Guid? clienteId, int pagina = 1, int tamanoPagina = 20,
            IMediator mediator = default!, CancellationToken cancellationToken = default) =>
            Results.Ok(await mediator.Send(
                new ObtenerCentrosQuery(
                    busqueda, clienteId,
                    Pagina: ApiV1.Pagina(pagina), TamanoPagina: ApiV1.TamanoPagina(tamanoPagina)),
                cancellationToken)));

        endpoints.MapGet("/centros/{id:guid}", async (Guid id, IMediator mediator, CancellationToken cancellationToken) =>
        {
            var centro = await mediator.Send(new ObtenerCentroPorIdQuery(id), cancellationToken);
            return centro is null ? Results.NotFound() : Results.Ok(centro);
        });

        return endpoints;
    }
}
