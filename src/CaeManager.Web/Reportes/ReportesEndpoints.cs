using CaeManager.Application.Reportes.Queries;
using CaeManager.Web.Features.Documentos;
using ClosedXML.Excel;
using MediatR;

namespace CaeManager.Web.Reportes;

public static class ReportesEndpoints
{
    public static IEndpointRouteBuilder MapReportesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/reportes/documentos.xlsx", async (IMediator mediator, CancellationToken cancellationToken) =>
        {
            var filas = await mediator.Send(new ObtenerReporteDocumentosQuery(), cancellationToken);

            using var libro = new XLWorkbook();
            var hoja = libro.Worksheets.Add("Documentos");

            hoja.Cell(1, 1).Value = "Estado";
            hoja.Cell(1, 2).Value = "Trabajador";
            hoja.Cell(1, 3).Value = "Empresa";
            hoja.Cell(1, 4).Value = "Tipo de documento";
            hoja.Cell(1, 5).Value = "Vencimiento";
            hoja.Row(1).Style.Font.Bold = true;

            var fila = 2;
            foreach (var documento in filas)
            {
                hoja.Cell(fila, 1).Value = EstadoDocumentoUi.Texto(documento.Estado);
                hoja.Cell(fila, 2).Value = documento.TrabajadorNombre;
                hoja.Cell(fila, 3).Value = documento.EmpresaRazonSocial;
                hoja.Cell(fila, 4).Value = documento.TipoDocumentoNombre;
                if (documento.FechaVencimiento is not null)
                    hoja.Cell(fila, 5).Value = documento.FechaVencimiento.Value.ToDateTime(TimeOnly.MinValue);
                fila++;
            }

            hoja.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            libro.SaveAs(stream);

            return Results.File(
                stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "reporte-documentos.xlsx");
        });

        endpoints.MapGet("/reportes/documentos.pdf", async (IMediator mediator, CancellationToken cancellationToken) =>
        {
            var filas = await mediator.Send(new ObtenerReporteDocumentosQuery(), cancellationToken);
            var bytes = GeneradorPdfReporteDocumentos.Generar(filas, DateTime.UtcNow);

            return Results.File(bytes, "application/pdf", "reporte-documentos.pdf");
        });

        return endpoints;
    }
}
