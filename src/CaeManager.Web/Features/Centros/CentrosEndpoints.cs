using CaeManager.Application.Centros.Queries.ObtenerCentros;
using ClosedXML.Excel;
using MediatR;

namespace CaeManager.Web.Features.Centros;

/// <summary>
/// Mismo patrón que ClientesEndpoints.cs. Complementa a
/// /asignaciones/exportar.xlsx (Lote 0-A): esto exporta la lista de Centros
/// en sí (estado, % cumplimiento), no sus asignaciones.
/// </summary>
public static class CentrosEndpoints
{
    public static IEndpointRouteBuilder MapCentrosEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/centros/exportar.xlsx", async (IMediator mediator, CancellationToken cancellationToken) =>
        {
            var resultado = await mediator.Send(
                new ObtenerCentrosQuery(Busqueda: null, ClienteId: null, Pagina: 1, TamanoPagina: int.MaxValue),
                cancellationToken);

            using var libro = new XLWorkbook();
            var hoja = libro.Worksheets.Add("Centros");

            hoja.Cell(1, 1).Value = "Nombre";
            hoja.Cell(1, 2).Value = "Código de centro";
            hoja.Cell(1, 3).Value = "Cliente";
            hoja.Cell(1, 4).Value = "Empresa";
            hoja.Cell(1, 5).Value = "Estado";
            hoja.Cell(1, 6).Value = "% cumplimiento";
            hoja.Row(1).Style.Font.Bold = true;

            var fila = 2;
            foreach (var centro in resultado.Elementos)
            {
                hoja.Cell(fila, 1).Value = centro.Nombre;
                hoja.Cell(fila, 2).Value = centro.CodigoCentro;
                hoja.Cell(fila, 3).Value = centro.ClienteRazonSocial;
                hoja.Cell(fila, 4).Value = centro.EmpresaRazonSocial;
                hoja.Cell(fila, 5).Value = EstadoCentroUi.Texto(centro.Estado);
                if (centro.CumplimientoPorcentaje is not null)
                    hoja.Cell(fila, 6).Value = centro.CumplimientoPorcentaje.Value / 100.0;
                fila++;
            }

            hoja.Column(6).Style.NumberFormat.Format = "0%";
            hoja.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            libro.SaveAs(stream);

            return Results.File(
                stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "centros.xlsx");
        });

        return endpoints;
    }
}
