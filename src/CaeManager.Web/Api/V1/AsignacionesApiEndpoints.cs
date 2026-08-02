using CaeManager.Application.Asignaciones.Queries.ObtenerAsignaciones;
using MediatR;

namespace CaeManager.Web.Api.V1;

public static class AsignacionesApiEndpoints
{
    public static IEndpointRouteBuilder MapAsignacionesApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // pagina/tamanoPagina llevan valor por defecto: sin él, Minimal API los
        // trata como parámetros de query obligatorios (verificado con curl).
        endpoints.MapGet("/asignaciones", async (
            string? busqueda, int pagina = 1, int tamanoPagina = 20,
            IMediator mediator = default!, CancellationToken cancellationToken = default) =>
            Results.Ok(await mediator.Send(
                new ObtenerAsignacionesQuery(busqueda, ApiV1.Pagina(pagina), ApiV1.TamanoPagina(tamanoPagina)),
                cancellationToken)));

        return endpoints;
    }
}
