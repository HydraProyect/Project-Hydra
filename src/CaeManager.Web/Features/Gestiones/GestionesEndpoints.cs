using CaeManager.Application.Common;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Domain.Gestiones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Exportacion;
using CaeManager.Web.Features.Gestiones.Recursos;
using ClosedXML.Excel;
using MediatR;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Gestiones;

/// <summary>Mismo patrón de referencia que TrabajadoresEndpoints — ver comentario allí.</summary>
public static class GestionesEndpoints
{
    public static IEndpointRouteBuilder MapGestionesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/gestiones/exportar.xlsx", ExportarAsync).SoloRolesDelListado();

        return endpoints;
    }

    /// <remarks>
    /// Los parámetros de consulta son los de «Exportar esta vista»: los mismos criterios que
    /// <c>Gestiones.razor.cs</c> pasa a <see cref="ObtenerGestionesQuery"/>, leídos igual que
    /// allí. Sin ninguno, es «Exportar todo». Las dos variantes pasan por esa misma consulta:
    /// su autorización, su alcance y su Tenant son los del listado. Cada fila nombra a un
    /// Trabajador, así que la descarga deja rastro.
    /// </remarks>
    public static async Task<IResult> ExportarAsync(
        IMediator mediator, IRegistroExportacionService registroExportacion,
        IStringLocalizer<TextosGestiones> textos, CancellationToken cancellationToken,
        string? q = null, string? estado = null, string? orden = null, bool desc = false)
    {
        // Como la página: la consulta filtra por un solo estado; con los dos marcados no hay
        // nada que filtrar.
        EstadoGestion? estadoAplicado = SeleccionEstados.Separar<EstadoGestion>(estado) is [var unico] ? unico : null;

        var gestiones = PaginadorExportacion.PaginarAsync((pagina, tamanoPagina) =>
            mediator.Send(
                new ObtenerGestionesQuery(
                    Busqueda: string.IsNullOrWhiteSpace(q) ? null : q,
                    Estado: estadoAplicado,
                    TrabajadorId: null,
                    Pagina: pagina,
                    TamanoPagina: tamanoPagina,
                    OrdenarPor: string.IsNullOrWhiteSpace(orden) ? null : orden,
                    Descendente: desc),
                cancellationToken));

        var (libro, filas) = await LibroDeListado.EscribirAsync(
            "Gestiones",
            [
                textos["ColumnaTrabajador"].Value, textos["ColumnaCentro"].Value, textos["ListaTipoDocumento"].Value,
                textos["ColumnaEstado"].Value, textos["ColumnaCreada"].Value
            ],
            FilasAsync(gestiones, textos), cancellationToken);

        await registroExportacion.RegistrarAsync(
            nameof(Gestion), filas,
            CriteriosExportacion.Desde(
                ("busqueda", string.IsNullOrWhiteSpace(q) ? null : "true"),
                ("estado", estadoAplicado?.ToString()),
                ("orden", CriteriosExportacion.SoloNombre(orden)), ("desc", desc ? "true" : null)),
            cancellationToken);

        return Results.File(libro, LibroDeListado.TipoContenido, "gestiones.xlsx");
    }

    private static async IAsyncEnumerable<IReadOnlyList<XLCellValue>> FilasAsync(
        IAsyncEnumerable<GestionListaDto> gestiones, IStringLocalizer<TextosGestiones> textos)
    {
        await foreach (var gestion in gestiones)
        {
            yield return
            [
                gestion.TrabajadorNombre, gestion.CentroNombre, gestion.TipoDocumentoNombre,
                textos[gestion.Estado == EstadoGestion.Completada ? "EstadoCompletada" : "EstadoPendiente"].Value,
                LibroDeListado.Fecha(DateOnly.FromDateTime(gestion.CreadoEnUtc))
            ];
        }
    }
}
