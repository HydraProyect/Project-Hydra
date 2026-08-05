using CaeManager.Application.Asignaciones.Queries.ObtenerAsignaciones;
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
            var resultado = await mediator.Send(
                new ObtenerAsignacionesQuery(Busqueda: null, Activa: true, Pagina: 1, TamanoPagina: int.MaxValue),
                cancellationToken);

            using var libro = new XLWorkbook();
            var hoja = libro.Worksheets.Add("Asignaciones");

            hoja.Cell(1, 1).Value = "Trabajador";
            hoja.Cell(1, 2).Value = "Centro";
            hoja.Cell(1, 3).Value = "Cliente";
            hoja.Cell(1, 4).Value = "Fecha de alta";
            hoja.Cell(1, 5).Value = "Estado";
            hoja.Row(1).Style.Font.Bold = true;

            var fila = 2;
            foreach (var asignacion in resultado.Elementos)
            {
                hoja.Cell(fila, 1).Value = asignacion.TrabajadorNombre;
                hoja.Cell(fila, 2).Value = asignacion.CentroNombre;
                hoja.Cell(fila, 3).Value = asignacion.ClienteNombre;
                hoja.Cell(fila, 4).Value = asignacion.FechaAlta.ToDateTime(TimeOnly.MinValue);
                hoja.Cell(fila, 5).Value = asignacion.FechaBaja is null ? "Activa" : $"Baja el {asignacion.FechaBaja:dd/MM/yyyy}";
                fila++;
            }

            hoja.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            libro.SaveAs(stream);

            return Results.File(
                stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "asignaciones.xlsx");
        });

        return endpoints;
    }
}
