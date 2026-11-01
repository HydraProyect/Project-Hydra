using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Common;
using CaeManager.Domain.ApiKeys;
using MediatR;
using System.Security.Claims;
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
            string? busqueda, bool? soloCriticos, ClaimsPrincipal usuario, int pagina = 1, int tamanoPagina = 20,
            IMediator mediator = default!, CancellationToken cancellationToken = default) =>
        {
            var resultado = await mediator.Send(
                new ObtenerClientesQuery(busqueda, soloCriticos, Pagina: ApiV1.Pagina(pagina), TamanoPagina: ApiV1.TamanoPagina(tamanoPagina)),
                cancellationToken);
            return Results.Ok(resultado);
        }).RequiereAlcanceIntegracion(AlcancesIntegracion.ClientesLeer);

        endpoints.MapGet("/clientes/{id:guid}", async (Guid id, IMediator mediator, CancellationToken cancellationToken) =>
        {
            var cliente = await mediator.Send(new ObtenerClientePorIdQuery(id), cancellationToken);
            return cliente is null ? Results.NotFound() : Results.Ok(ClienteApiDetalleDto.DesdeInterno(cliente));
        }).RequiereAlcanceIntegracion(AlcancesIntegracion.ClientesLeer);

        return endpoints;
    }
}

/// <summary>
/// Lo que ve la Clave de Integración del Tenant en la lista. Sin el estado documental ni su recuento: agregan las
/// alertas de todos los documentos de los Trabajadores, y en un Cliente empresarial pequeño delatarían los de salud.
/// </summary>
public record ClienteApiListaDto(
    Guid Id, string RazonSocial, string Cif, bool EsCritico, DateTime CreadoEnUtc,
    [property: JsonPropertyName("ejecutivoUsuarioId")] Guid? GestorCaeUsuarioId, int Centros)
{
    public static ClienteApiListaDto DesdeInterno(ClienteListaDto dto)
    {
        var (id, razonSocial, cif, esCritico, creadoEnUtc, _, gestorCaeUsuarioId, centros, _, _) = dto;
        return new(id, razonSocial, cif, esCritico, creadoEnUtc, gestorCaeUsuarioId, centros);
    }

    public static ResultadoPaginado<ClienteApiListaDto> DesdeInterno(ResultadoPaginado<ClienteListaDto> resultado) =>
        new(resultado.Elementos.Select(DesdeInterno).ToList(), resultado.TotalElementos, resultado.Pagina, resultado.TamanoPagina);
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
