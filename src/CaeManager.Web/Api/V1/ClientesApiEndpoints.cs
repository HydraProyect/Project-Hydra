using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using MediatR;
using System.Text.Json.Serialization;

namespace CaeManager.Web.Api.V1;

public static class ClientesApiEndpoints
{
    public static IEndpointRouteBuilder MapClientesApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // pagina/tamanoPagina llevan valor por defecto: sin él, Minimal API
        // los trata como parámetros de query obligatorios (verificado con
        // curl — sin ellos, cualquier llamada sin paginar explícita
        // devolvía 400).
        endpoints.MapGet("/clientes", async (
            string? busqueda, bool? soloCriticos, int pagina = 1, int tamanoPagina = 20,
            IMediator mediator = default!, CancellationToken cancellationToken = default) =>
            Results.Ok(await mediator.Send(
                new ObtenerClientesQuery(busqueda, soloCriticos, Pagina: ApiV1.Pagina(pagina), TamanoPagina: ApiV1.TamanoPagina(tamanoPagina)),
                cancellationToken)));

        endpoints.MapGet("/clientes/{id:guid}", async (Guid id, IMediator mediator, CancellationToken cancellationToken) =>
        {
            var cliente = await mediator.Send(new ObtenerClientePorIdQuery(id), cancellationToken);
            return cliente is null ? Results.NotFound() : Results.Ok(ClienteApiDetalleDto.DesdeInterno(cliente));
        });

        return endpoints;
    }
}

public record ClienteApiDetalleDto(
    Guid Id, string RazonSocial, string Cif, bool EsCritico, DateTime CreadoEnUtc,
    [property: JsonPropertyName("ejecutivoUsuarioId")] Guid? GestorCaeUsuarioId, Guid Version)
{
    public static ClienteApiDetalleDto DesdeInterno(ClienteDetalleDto dto)
    {
        var (id, razonSocial, cif, esCritico, _, creadoEnUtc, gestorCaeUsuarioId, version, _) = dto;
        return new(id, razonSocial, cif, esCritico, creadoEnUtc, gestorCaeUsuarioId, version);
    }
}
