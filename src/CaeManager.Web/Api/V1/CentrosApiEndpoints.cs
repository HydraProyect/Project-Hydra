using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Domain.Centros;
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
            return centro is null ? Results.NotFound() : Results.Ok(CentroApiDetalleDto.DesdeInterno(centro));
        });

        return endpoints;
    }
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
