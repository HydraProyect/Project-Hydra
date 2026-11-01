using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Common;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Centros;
using MediatR;
using System.Security.Claims;
using System.Text.Json.Serialization;

namespace CaeManager.Web.Api.V1;

public static class CentrosApiEndpoints
{
    public static IEndpointRouteBuilder MapCentrosApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // pagina/tamanoPagina llevan valor por defecto: sin él, Minimal API los
        // trata como parámetros de query obligatorios (verificado con curl).
        endpoints.MapGet("/centros", async (
            string? busqueda, Guid? clienteId, ClaimsPrincipal usuario, int pagina = 1, int tamanoPagina = 20,
            IMediator mediator = default!, CancellationToken cancellationToken = default) =>
        {
            var esSoporte = ApiV1.EsClaveDeSoporte(usuario);
            var resultado = await mediator.Send(
                new ObtenerCentrosQuery(
                    busqueda, clienteId,
                    Pagina: ApiV1.Pagina(pagina), TamanoPagina: ApiV1.TamanoPagina(tamanoPagina),
                    ConIncidencias: esSoporte),
                cancellationToken);
            return Results.Ok(resultado);
        }).RequiereAlcanceIntegracion(AlcancesIntegracion.CentrosLeer);

        endpoints.MapGet("/centros/{id:guid}", async (Guid id, IMediator mediator, CancellationToken cancellationToken) =>
        {
            var centro = await mediator.Send(new ObtenerCentroPorIdQuery(id), cancellationToken);
            return centro is null ? Results.NotFound() : Results.Ok(CentroApiDetalleDto.DesdeInterno(centro));
        }).RequiereAlcanceIntegracion(AlcancesIntegracion.CentrosLeer);

        return endpoints;
    }
}

/// <summary>
/// Lo que ve la Clave de Integración del Tenant en la lista. Sin estado, cumplimiento ni incidencias: salen de todos
/// los documentos del Centro, y las incidencias nombran el documento, su vencimiento y al Trabajador.
/// </summary>
public record CentroApiListaDto(
    Guid Id, string Nombre, string? CodigoCentro,
    [property: JsonPropertyName("clienteId")] Guid ClienteEmpresarialId, string ClienteRazonSocial,
    Guid EmpresaId, string EmpresaRazonSocial)
{
    public static CentroApiListaDto DesdeInterno(CentroListaDto dto)
    {
        var (id, nombre, codigoCentro, clienteEmpresarialId, clienteRazonSocial, empresaId, empresaRazonSocial, _, _, _) = dto;
        return new(id, nombre, codigoCentro, clienteEmpresarialId, clienteRazonSocial, empresaId, empresaRazonSocial);
    }

    public static ResultadoPaginado<CentroApiListaDto> DesdeInterno(ResultadoPaginado<CentroListaDto> resultado) =>
        new(resultado.Elementos.Select(DesdeInterno).ToList(), resultado.TotalElementos, resultado.Pagina, resultado.TamanoPagina);
}

/// <summary>
/// Contrato público previo de detalle de Centro. La ficha de acceso y prevención de Centro 360 (horario, contactos
/// de control de accesos y del Técnico de prevención, trabajos especiales) no se publica en la API v1: lleva datos
/// personales que la aplicación solo enseña al equipo de gestión y que la auditoría enmascara.
/// </summary>
public record CentroApiDetalleDto(Guid Id, Guid ClienteId, string ClienteRazonSocial, Guid EmpresaId,
    string EmpresaRazonSocial, string Nombre, string? CodigoCentro, string? Direccion, string? Contacto,
    DateOnly? ContratoVigenteHasta, Guid Version, ModalidadGestionCae GestionCae)
{
    public static CentroApiDetalleDto DesdeInterno(CentroDetalleDto dto) =>
        new(dto.Id, dto.ClienteId, dto.ClienteRazonSocial, dto.EmpresaId, dto.EmpresaRazonSocial, dto.Nombre,
            dto.CodigoCentro, dto.Direccion, dto.Contacto, dto.ContratoVigenteHasta, dto.Version, dto.GestionCae);
}
