using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculos;
using CaeManager.Domain.Vehiculos;
using CaeManager.Web.Exportacion;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Features.Vehiculos.Recursos;
using ClosedXML.Excel;
using MediatR;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Vehiculos;

/// <summary>Mismo patrón de referencia que TrabajadoresEndpoints — ver comentario allí.</summary>
public static class VehiculosEndpoints
{
    public static IEndpointRouteBuilder MapVehiculosEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/vehiculos/exportar.xlsx", ExportarAsync).SoloRolesDelListado();

        return endpoints;
    }

    /// <remarks>
    /// Los parámetros de consulta son los de «Exportar esta vista»: los mismos criterios que
    /// <c>Vehiculos.razor.cs</c> pasa a <see cref="ObtenerVehiculosQuery"/>, leídos igual que
    /// allí. Sin ninguno, es «Exportar todo». Las dos variantes pasan por esa misma consulta:
    /// su autorización, su alcance y su Tenant son los del listado. Las columnas son las del
    /// listado más el modelo, que la pantalla enseña bajo el nombre.
    /// </remarks>
    public static async Task<IResult> ExportarAsync(
        IMediator mediator, ITenantActual tenantActual, IRegistroExportacionService registroExportacion,
        IStringLocalizer<TextosVehiculos> textos, CancellationToken cancellationToken,
        string? q = null, string? estado = null, string? empresa = null, string? subcontrata = null,
        string? orden = null, bool desc = false)
    {
        // Estado 4a, igual que /trabajadores/exportar.xlsx: quien tiene que elegir una empresa
        // de su cartera no ve en la página los datos del Tenant de origen y tampoco los exporta.
        var autorizados = await mediator.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);
        if (ClientesAutorizados.PideElegirEmpresa(autorizados, tenantActual.TenantId))
            return Results.Redirect("/vehiculos");

        var vehiculos = PaginadorExportacion.PaginarAsync((pagina, tamanoPagina) =>
            mediator.Send(
                new ObtenerVehiculosQuery(
                    Busqueda: string.IsNullOrWhiteSpace(q) ? null : q,
                    EmpresaId: Guid.TryParse(empresa, out var empresaId) ? empresaId : null,
                    SubcontrataId: Guid.TryParse(subcontrata, out var subcontrataId) ? subcontrataId : null,
                    Pagina: pagina,
                    TamanoPagina: tamanoPagina,
                    OrdenarPor: string.IsNullOrWhiteSpace(orden) ? null : orden,
                    Descendente: desc,
                    EstadoDocumental: string.IsNullOrWhiteSpace(estado) ? null : estado),
                cancellationToken));

        var (libro, filas) = await LibroDeListado.EscribirAsync(
            "Vehículos",
            [
                textos["EtiquetaNombre"].Value, textos["EtiquetaModelo"].Value, textos["EtiquetaMatricula"].Value,
                textos["ColumnaEmpleador"].Value, textos["EtiquetaDocumentacion"].Value
            ],
            FilasAsync(vehiculos), cancellationToken);

        // Rastro ANTES de entregar, con fallo cerrado. Va el criterio aplicado, nunca el texto
        // de la petición (ver TrabajadoresEndpoints).
        await registroExportacion.RegistrarAsync(
            nameof(Vehiculo), filas,
            CriteriosExportacion.Desde(
                ("busqueda", string.IsNullOrWhiteSpace(q) ? null : "true"),
                ("estado", CriteriosExportacion.SoloNombres(estado)),
                ("empresa", Guid.TryParse(empresa, out var empresaAplicada) ? empresaAplicada.ToString() : null),
                ("subcontrata", Guid.TryParse(subcontrata, out var subcontrataAplicada) ? subcontrataAplicada.ToString() : null),
                ("orden", CriteriosExportacion.SoloNombre(orden)), ("desc", desc ? "true" : null)),
            cancellationToken);

        return Results.File(libro, LibroDeListado.TipoContenido, "vehiculos.xlsx");
    }

    private static async IAsyncEnumerable<IReadOnlyList<XLCellValue>> FilasAsync(IAsyncEnumerable<VehiculoListaDto> vehiculos)
    {
        await foreach (var vehiculo in vehiculos)
        {
            yield return
            [
                vehiculo.Nombre, vehiculo.Modelo, vehiculo.NumeroPlaca, vehiculo.EmpleadorNombre,
                EstadoDocumentoUi.TextoDocumental(vehiculo.EstadoDocumental)
            ];
        }
    }
}
