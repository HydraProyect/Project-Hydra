using CaeManager.Application.Common;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignaciones;
using CaeManager.Web.Exportacion;
using CaeManager.Web.Features.Asignaciones.Recursos;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Asignaciones;
using ClosedXML.Excel;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Asignaciones;

/// <summary>
/// Export plano de todas las asignaciones activas — Centro 360 (Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md
/// § 0.1) sustituye a /asignaciones por el acordeón de /centros, pero se
/// conserva este dato en tabla para auditoría/"dónde está Juan hoy", que no
/// siempre se responde mejor por-centro. Mismo patrón que ClientesEndpoints.cs.
/// </summary>
public static class AsignacionesEndpoints
{
    public static IEndpointRouteBuilder MapAsignacionesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/asignaciones/exportar.xlsx", ExportarAsync);

        return endpoints;
    }

    public static async Task<IResult> ExportarAsync(
        IMediator mediator, [FromServices] ISesionPrivilegiadaActual sesionPrivilegiada,
        [Microsoft.AspNetCore.Mvc.FromServices] IRegistroExportacionService registroExportacion,
        [FromServices] IStringLocalizer<TextosAsignaciones> textos, CancellationToken cancellationToken)
    {
        // Soporte TALVEG no escribe en RegistrosAuditoria: lo que no deja rastro no se entrega.
        if (await sesionPrivilegiada.ObtenerAsync(cancellationToken) is not null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        // Pagina en lotes en vez de TamanoPagina: int.MaxValue (P2.7):
        // no materializa toda la tabla de asignaciones del tenant de golpe.
        var asignaciones = PaginadorExportacion.PaginarAsync((pagina, tamanoPagina) =>
            mediator.Send(
                new ObtenerAsignacionesQuery(Busqueda: null, Activa: true, Pagina: pagina, TamanoPagina: tamanoPagina),
                cancellationToken));

        using var libro = await ConstruirLibroAsync(asignaciones, textos);
        var filas = libro.Worksheets.First().RowsUsed().Count() - 1;

        // Se escribe directo en el stream que consume la respuesta HTTP
        // (Results.File lo cierra) en vez de bufferear en un MemoryStream
        // y duplicarlo otra vez con ToArray().
        var stream = new MemoryStream();
        libro.SaveAs(stream);
        stream.Position = 0;

        try
        {
            await registroExportacion.RegistrarAsync(
                nameof(Asignacion), filas,
                CriteriosExportacion.Desde(("listado", "asignaciones"), ("activa", "true")),
                cancellationToken);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }

        return Results.File(
            stream,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "asignaciones.xlsx");
    }

    /// <summary>
    /// Arma el libro con los textos de <see cref="TextosAsignaciones"/> en la
    /// cultura de la petición. Pública solo para poder testearla directamente
    /// (sin InternalsVisibleTo en CaeManager.Web).
    /// </summary>
    public static async Task<XLWorkbook> ConstruirLibroAsync(
        IAsyncEnumerable<AsignacionListaDto> asignaciones, IStringLocalizer<TextosAsignaciones> textos)
    {
        var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add(textos["NombreHoja"]);

        hoja.Cell(1, 1).Value = textos["ColumnaTrabajador"].Value;
        hoja.Cell(1, 2).Value = textos["ColumnaCentro"].Value;
        hoja.Cell(1, 3).Value = textos["ColumnaCliente"].Value;
        hoja.Cell(1, 4).Value = textos["ColumnaFechaAlta"].Value;
        hoja.Cell(1, 5).Value = textos["ColumnaEstado"].Value;
        hoja.Row(1).Style.Font.Bold = true;

        var fila = 2;
        await foreach (var asignacion in asignaciones)
        {
            hoja.Cell(fila, 1).Value = asignacion.TrabajadorNombre;
            hoja.Cell(fila, 2).Value = asignacion.CentroNombre;
            hoja.Cell(fila, 3).Value = asignacion.ClienteNombre;
            hoja.Cell(fila, 4).Value = asignacion.FechaAlta.ToDateTime(TimeOnly.MinValue);
            hoja.Cell(fila, 5).Value = asignacion.FechaBaja is { } fechaBaja
                ? textos["EstadoBajaEl", fechaBaja].Value
                : textos["EstadoActiva"].Value;
            fila++;
        }

        hoja.Columns().AdjustToContents();
        return libro;
    }
}
