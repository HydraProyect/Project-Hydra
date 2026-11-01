using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.ApiKeys;
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

/// <summary>Contrato público previo de detalle; los datos nuevos de la ficha no se publican en la API v1.</summary>
public record TrabajadorApiDetalleDto(Guid Id, Guid? EmpresaId, Guid? SubcontrataId,
    string EmpleadorNombre, string Nombre, string Apellidos, string? Dni, DateOnly? FechaNacimiento,
    string? Email, string? Observaciones, string? Alias, string? Telefono, string? Puesto, Guid Version)
{
    public static TrabajadorApiDetalleDto DesdeInterno(TrabajadorDetalleDto dto) =>
        new(dto.Id, dto.EmpresaId, dto.SubcontrataId, dto.EmpleadorNombre, dto.Nombre, dto.Apellidos,
            dto.Dni, dto.FechaNacimiento, dto.Email, dto.Observaciones, dto.Alias, dto.Telefono, dto.Puesto, dto.Version);
}

/// <summary>
/// Lo que ve una Clave de Integración del Tenant con <c>trabajadores.leer</c> y sin
/// <c>trabajadores.datos-personales.leer</c>. Es otro tipo, no el anterior con nulos: el campo no concedido
/// no llega a existir en la respuesta (OWASP API3:2023). Tampoco el estado documental ni sus recuentos, con
/// ningún alcance: se calculan con todos los documentos, y un reconocimiento médico caducado se leería en ellos.
/// </summary>
public record TrabajadorApiListaSinDatosPersonalesDto(Guid Id, string Nombre, string Apellidos, string EmpleadorNombre)
{
    public static TrabajadorApiListaSinDatosPersonalesDto DesdeInterno(TrabajadorListaDto dto) =>
        new(dto.Id, dto.Nombre, dto.Apellidos, dto.EmpleadorNombre);

    public static ResultadoPaginado<TrabajadorApiListaSinDatosPersonalesDto> DesdeInterno(ResultadoPaginado<TrabajadorListaDto> resultado) =>
        new(resultado.Elementos.Select(DesdeInterno).ToList(), resultado.TotalElementos, resultado.Pagina, resultado.TamanoPagina);
}

/// <summary>Lo que ve una Clave de Integración del Tenant que además tiene <c>trabajadores.datos-personales.leer</c>.</summary>
public record TrabajadorApiListaConDatosPersonalesDto(Guid Id, string Nombre, string Apellidos, string? Dni, string EmpleadorNombre)
{
    public static TrabajadorApiListaConDatosPersonalesDto DesdeInterno(TrabajadorListaDto dto) =>
        new(dto.Id, dto.Nombre, dto.Apellidos, dto.Dni, dto.EmpleadorNombre);

    public static ResultadoPaginado<TrabajadorApiListaConDatosPersonalesDto> DesdeInterno(ResultadoPaginado<TrabajadorListaDto> resultado) =>
        new(resultado.Elementos.Select(DesdeInterno).ToList(), resultado.TotalElementos, resultado.Pagina, resultado.TamanoPagina);
}

/// <summary>Mismo criterio que <see cref="TrabajadorApiListaSinDatosPersonalesDto"/>, en el detalle.</summary>
public record TrabajadorApiDetalleSinDatosPersonalesDto(Guid Id, Guid? EmpresaId, Guid? SubcontrataId,
    string EmpleadorNombre, string Nombre, string Apellidos, string? Alias, string? Puesto, Guid Version)
{
    public static TrabajadorApiDetalleSinDatosPersonalesDto DesdeInterno(TrabajadorDetalleDto dto) =>
        new(dto.Id, dto.EmpresaId, dto.SubcontrataId, dto.EmpleadorNombre, dto.Nombre, dto.Apellidos, dto.Alias, dto.Puesto, dto.Version);
}

public static class TrabajadoresApiEndpoints
{
    public static IEndpointRouteBuilder MapTrabajadoresApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // pagina/tamanoPagina llevan valor por defecto: sin él, Minimal API los
        // trata como parámetros de query obligatorios (verificado con curl).
        endpoints.MapGet("/trabajadores", async (
            string? busqueda, Guid? empresaId, Guid? subcontrataId, int pagina = 1, int tamanoPagina = 20,
            IMediator mediator = default!, ClaimsPrincipal usuario = default!, CancellationToken cancellationToken = default) =>
        {
            var veDatosPersonales = ApiV1.VeDatosPersonalesDeTrabajador(usuario);
            var resultado = await mediator.Send(
                new ObtenerTrabajadoresQuery(
                    // Sin ConDesgloseDocumental: el desglose no se publica (TrabajadorApiListaDto), así que no se pide.
                    busqueda, empresaId, subcontrataId, ApiV1.Pagina(pagina), ApiV1.TamanoPagina(tamanoPagina),
                    BusquedaSinDni: false),
                cancellationToken);

            if (ApiV1.EsClaveDeSoporte(usuario))
                return Results.Ok(TrabajadorApiListaDto.DesdeInterno(resultado));

            return veDatosPersonales
                ? Results.Ok(TrabajadorApiListaConDatosPersonalesDto.DesdeInterno(resultado))
                : Results.Ok(TrabajadorApiListaSinDatosPersonalesDto.DesdeInterno(resultado));
        }).RequiereAlcanceIntegracion(AlcancesIntegracion.TrabajadoresLeer);

        endpoints.MapGet("/trabajadores/{id:guid}", async (Guid id, ClaimsPrincipal usuario, IMediator mediator, CancellationToken cancellationToken) =>
        {
            var trabajador = await mediator.Send(new ObtenerTrabajadorPorIdQuery(id), cancellationToken);
            if (trabajador is null) return Results.NotFound();

            return ApiV1.VeDatosPersonalesDeTrabajador(usuario)
                ? Results.Ok(TrabajadorApiDetalleDto.DesdeInterno(trabajador))
                : Results.Ok(TrabajadorApiDetalleSinDatosPersonalesDto.DesdeInterno(trabajador));
        }).RequiereAlcanceIntegracion(AlcancesIntegracion.TrabajadoresLeer);

        return endpoints;
    }
}
