using System.Security.Claims;
using System.Text.Json.Serialization;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignaciones;
using CaeManager.Application.Common;
using CaeManager.Domain.ApiKeys;
using MediatR;

namespace CaeManager.Web.Api.V1;

public static class AsignacionesApiEndpoints
{
    public static IEndpointRouteBuilder MapAsignacionesApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // pagina/tamanoPagina llevan valor por defecto: sin él, Minimal API los
        // trata como parámetros de query obligatorios (verificado con curl).
        endpoints.MapGet("/asignaciones", async (
            string? busqueda, ClaimsPrincipal usuario, int pagina = 1, int tamanoPagina = 20,
            IMediator mediator = default!, CancellationToken cancellationToken = default) =>
        {
            var resultado = await mediator.Send(
                new ObtenerAsignacionesQuery(
                    busqueda, Pagina: ApiV1.Pagina(pagina), TamanoPagina: ApiV1.TamanoPagina(tamanoPagina)),
                cancellationToken);
            return ApiV1.EsClaveDeSoporte(usuario) ? Results.Ok(resultado) : Results.Ok(AsignacionApiListaDto.DesdeInterno(resultado));
        }).RequiereAlcanceIntegracion(AlcancesIntegracion.AsignacionesLeer);

        return endpoints;
    }
}

/// <summary>
/// El contrato público de la lista. Hoy coincide campo a campo con <see cref="AsignacionListaDto"/>: un tipo
/// propio para que añadir un campo interno no lo publique a la Clave de Integración del Tenant.
/// </summary>
public record AsignacionApiListaDto(
    Guid Id, Guid TrabajadorId, string TrabajadorNombre, Guid CentroId, string CentroNombre,
    [property: JsonPropertyName("clienteId")] Guid ClienteEmpresarialId,
    string ClienteNombre, DateOnly FechaAlta, DateOnly? FechaBaja)
{
    public static AsignacionApiListaDto DesdeInterno(AsignacionListaDto dto)
    {
        var (id, trabajadorId, trabajadorNombre, centroId, centroNombre, clienteEmpresarialId, clienteNombre, fechaAlta, fechaBaja) = dto;
        return new(id, trabajadorId, trabajadorNombre, centroId, centroNombre, clienteEmpresarialId, clienteNombre, fechaAlta, fechaBaja);
    }

    public static ResultadoPaginado<AsignacionApiListaDto> DesdeInterno(ResultadoPaginado<AsignacionListaDto> resultado) =>
        new(resultado.Elementos.Select(DesdeInterno).ToList(), resultado.TotalElementos, resultado.Pagina, resultado.TamanoPagina);
}
