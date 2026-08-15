using CaeManager.Application.Asignaciones.Queries.ObtenerAsignaciones;
using CaeManager.Web.Exportacion;
using ClosedXML.Excel;
using MediatR;

namespace CaeManager.Web.Features.Asignaciones;

/// <summary>
/// Export plano de todas las asignaciones activas — Centro 360 (PLAN-EJECUCION-UX.md
/// § 0.1) sustituye a /asignaciones por el acordeón de /centros, pero se
/// conserva este dato en tabla para auditoría/"dónde está Juan hoy", que no
/// siempre se responde mejor por-centro. Mismo patrón que ClientesEndpoints.cs.
/// </summary>
public static class AsignacionesEndpoints
{
    public static IEndpointRouteBuilder MapAsignacionesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/asignaciones/exportar.xlsx", async (IMediator mediator, CancellationToken cancellationToken) =>
        {
            using var libro = new XLWorkbook();
            var hoja = libro.Worksheets.Add("Asignaciones");

            hoja.Cell(1, 1).Value = "Trabajador";
            hoja.Cell(1, 2).Value = "Centro";
            hoja.Cell(1, 3).Value = "Cliente";
            hoja.Cell(1, 4).Value = "Fecha de alta";
            hoja.Cell(1, 5).Value = "Estado";
            hoja.Row(1).Style.Font.Bold = true;

            // Pagina en lotes en vez de TamanoPagina: int.MaxValue (P2.7):
            // no materializa toda la tabla de asignaciones del tenant de golpe.
            var fila = 2;
            await foreach (var asignacion in PaginadorExportacion.PaginarAsync((pagina, tamanoPagina) =>
                mediator.Send(
                    new ObtenerAsignacionesQuery(Busqueda: null, Activa: true, Pagina: pagina, TamanoPagina: tamanoPagina),
                    cancellationToken)))
            {
                hoja.Cell(fila, 1).Value = asignacion.TrabajadorNombre;
                hoja.Cell(fila, 2).Value = asignacion.CentroNombre;
                hoja.Cell(fila, 3).Value = asignacion.ClienteNombre;
                hoja.Cell(fila, 4).Value = asignacion.FechaAlta.ToDateTime(TimeOnly.MinValue);
                hoja.Cell(fila, 5).Value = asignacion.FechaBaja is null ? "Activa" : $"Baja el {asignacion.FechaBaja:dd/MM/yyyy}";
                fila++;
            }

            hoja.Columns().AdjustToContents();

            // Se escribe directo en el stream que consume la respuesta HTTP
            // (Results.File lo cierra) en vez de bufferear en un MemoryStream
            // y duplicarlo otra vez con ToArray().
            var stream = new MemoryStream();
            libro.SaveAs(stream);
            stream.Position = 0;

            return Results.File(
                stream,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "asignaciones.xlsx");
        });

        return endpoints;
    }
}
