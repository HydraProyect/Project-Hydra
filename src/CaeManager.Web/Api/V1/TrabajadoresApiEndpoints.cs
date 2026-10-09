using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Documentos;
using MediatR;

namespace CaeManager.Web.Api.V1;

/// <summary>
/// Proyección pública de <see cref="TrabajadorListaDto"/> para <c>GET /api/v1/trabajadores</c>: los seis
/// campos que la API ha devuelto siempre, ni uno más. El DTO interno es el de la pantalla y crece con ella
/// (incidencias documentales, «vigentes / registrados»); la API v1 es un contrato publicado y no gana campos
/// porque los gane un listado. Mismo criterio que <see cref="DocumentoApiListaDto"/>. Lo fija
/// <c>TrabajadorApiDtosTests</c>.
/// </summary>
public record TrabajadorApiListaDto(
    Guid Id, string Nombre, string Apellidos, string? Dni, string EmpleadorNombre, EstadoDocumento? EstadoDocumental)
{
    public static TrabajadorApiListaDto DesdeInterno(TrabajadorListaDto dto) =>
        new(dto.Id, dto.Nombre, dto.Apellidos, dto.Dni, dto.EmpleadorNombre, dto.EstadoDocumental);

    /// <summary>La página entera, con el mismo envoltorio que devolvía el DTO interno.</summary>
    public static ResultadoPaginado<TrabajadorApiListaDto> DesdeInterno(ResultadoPaginado<TrabajadorListaDto> resultado) =>
        new(resultado.Elementos.Select(DesdeInterno).ToList(), resultado.TotalElementos, resultado.Pagina, resultado.TamanoPagina)
        {
            RecuentosPorEstado = resultado.RecuentosPorEstado,
            TotalSinFiltroDeEstado = resultado.TotalSinFiltroDeEstado
        };
}

public static class TrabajadoresApiEndpoints
{
    public static IEndpointRouteBuilder MapTrabajadoresApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // pagina/tamanoPagina llevan valor por defecto: sin él, Minimal API los
        // trata como parámetros de query obligatorios (verificado con curl).
        endpoints.MapGet("/trabajadores", async (
            string? busqueda, Guid? empresaId, Guid? subcontrataId, int pagina = 1, int tamanoPagina = 20,
            IMediator mediator = default!, CancellationToken cancellationToken = default) =>
            Results.Ok(TrabajadorApiListaDto.DesdeInterno(await mediator.Send(
                new ObtenerTrabajadoresQuery(
                    busqueda, empresaId, subcontrataId, ApiV1.Pagina(pagina), ApiV1.TamanoPagina(tamanoPagina),
                    // El desglose documental no se publica (TrabajadorApiListaDto): no se calcula.
                    ConDesgloseDocumental: false),
                cancellationToken))));

        endpoints.MapGet("/trabajadores/{id:guid}", async (Guid id, IMediator mediator, CancellationToken cancellationToken) =>
        {
            var trabajador = await mediator.Send(new ObtenerTrabajadorPorIdQuery(id), cancellationToken);
            return trabajador is null ? Results.NotFound() : Results.Ok(trabajador);
        });

        return endpoints;
    }
}
