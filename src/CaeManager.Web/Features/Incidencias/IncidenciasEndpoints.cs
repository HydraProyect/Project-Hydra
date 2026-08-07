using CaeManager.Application.Incidencias.Queries.ObtenerIncidencias;
using CaeManager.Domain.Incidencias;
using ClosedXML.Excel;
using MediatR;

namespace CaeManager.Web.Features.Incidencias;

/// <summary>
/// Mismo patrón que ClientesEndpoints.cs (docs/ux-audit/08-visitas-gestiones-incidencias-evaluaciones.md
/// H4 — valor probatorio, prioridad sobre las otras tres listas sin export).
/// </summary>
public static class IncidenciasEndpoints
{
    public static IEndpointRouteBuilder MapIncidenciasEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/incidencias/exportar.xlsx", async (IMediator mediator, CancellationToken cancellationToken) =>
        {
            var resultado = await mediator.Send(
                new ObtenerIncidenciasQuery(Busqueda: null, SoloSinResolver: false, Pagina: 1, TamanoPagina: int.MaxValue),
                cancellationToken);

            using var libro = new XLWorkbook();
            var hoja = libro.Worksheets.Add("Incidencias");

            hoja.Cell(1, 1).Value = "Centro";
            hoja.Cell(1, 2).Value = "Trabajador";
            hoja.Cell(1, 3).Value = "Tipo";
            hoja.Cell(1, 4).Value = "Gravedad";
            hoja.Cell(1, 5).Value = "Fecha de ocurrencia";
            hoja.Cell(1, 6).Value = "Estado";
            hoja.Row(1).Style.Font.Bold = true;

            var fila = 2;
            foreach (var incidencia in resultado.Elementos)
            {
                hoja.Cell(fila, 1).Value = incidencia.CentroNombre;
                hoja.Cell(fila, 2).Value = incidencia.TrabajadorNombre;
                hoja.Cell(fila, 3).Value = TextoTipo(incidencia.Tipo);
                hoja.Cell(fila, 4).Value = TextoGravedad(incidencia.Gravedad);
                hoja.Cell(fila, 5).Value = incidencia.FechaOcurrencia.ToDateTime(TimeOnly.MinValue);
                hoja.Cell(fila, 6).Value = incidencia.Resuelta ? "Resuelta" : "Sin resolver";
                fila++;
            }

            hoja.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            libro.SaveAs(stream);

            return Results.File(
                stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "incidencias.xlsx");
        });

        return endpoints;
    }

    private static string TextoTipo(TipoIncidencia tipo) => tipo switch
    {
        TipoIncidencia.Accidente => "Accidente",
        TipoIncidencia.Incumplimiento => "Incumplimiento",
        _ => tipo.ToString()
    };

    private static string TextoGravedad(GravedadIncidencia gravedad) => gravedad switch
    {
        GravedadIncidencia.Leve => "Leve",
        GravedadIncidencia.Grave => "Grave",
        GravedadIncidencia.MuyGrave => "Muy grave",
        _ => gravedad.ToString()
    };
}
