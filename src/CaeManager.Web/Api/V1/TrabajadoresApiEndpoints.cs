using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using MediatR;

namespace CaeManager.Web.Api.V1;

public static class TrabajadoresApiEndpoints
{
    public static IEndpointRouteBuilder MapTrabajadoresApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // pagina/tamanoPagina llevan valor por defecto: sin él, Minimal API los
        // trata como parámetros de query obligatorios (verificado con curl).
        endpoints.MapGet("/trabajadores", async (
            string? busqueda, Guid? empresaId, Guid? subcontrataId, int pagina = 1, int tamanoPagina = 20,
            IMediator mediator = default!, CancellationToken cancellationToken = default) =>
            Results.Ok(await mediator.Send(
                new ObtenerTrabajadoresQuery(busqueda, empresaId, subcontrataId, ApiV1.Pagina(pagina), ApiV1.TamanoPagina(tamanoPagina)),
                cancellationToken)));

        endpoints.MapGet("/trabajadores/{id:guid}", async (Guid id, IMediator mediator, CancellationToken cancellationToken) =>
        {
            var trabajador = await mediator.Send(new ObtenerTrabajadorPorIdQuery(id), cancellationToken);
            return trabajador is null ? Results.NotFound() : Results.Ok(trabajador);
        });

        return endpoints;
    }
}
