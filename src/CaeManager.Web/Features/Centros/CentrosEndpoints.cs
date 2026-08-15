using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Web.Exportacion;
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
            using var libro = new XLWorkbook();
            var hoja = libro.Worksheets.Add("Centros");

            hoja.Cell(1, 1).Value = "Nombre";
            hoja.Cell(1, 2).Value = "Código de centro";
            hoja.Cell(1, 3).Value = "Cliente";
            hoja.Cell(1, 4).Value = "Empresa";
            hoja.Cell(1, 5).Value = "Estado";
            hoja.Cell(1, 6).Value = "% cumplimiento";
            hoja.Row(1).Style.Font.Bold = true;
            hoja.Column(6).Style.NumberFormat.Format = "0%";

            // Pagina en lotes en vez de TamanoPagina: int.MaxValue (P2.7):
            // no materializa toda la tabla de centros del tenant de golpe.
            var fila = 2;
            await foreach (var centro in PaginadorExportacion.PaginarAsync((pagina, tamanoPagina) =>
                mediator.Send(
                    new ObtenerCentrosQuery(Busqueda: null, ClienteId: null, Pagina: pagina, TamanoPagina: tamanoPagina),
                    cancellationToken)))
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
                "centros.xlsx");
        });

        return endpoints;
    }
}
