using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Web.Exportacion;
using CaeManager.Web.Features.Documentos;
using ClosedXML.Excel;
using MediatR;

namespace CaeManager.Web.Features.Empresas;

/// <summary>
/// Mismo patrón que ClientesEndpoints.cs (docs/ux-audit/03-empresas-subcontratas.md H7).
/// </summary>
public static class EmpresasEndpoints
{
    public static IEndpointRouteBuilder MapEmpresasEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/empresas/exportar.xlsx", async (IMediator mediator, CancellationToken cancellationToken) =>
        {
            using var libro = new XLWorkbook();
            var hoja = libro.Worksheets.Add("Empresas");

            hoja.Cell(1, 1).Value = "Razón social";
            hoja.Cell(1, 2).Value = "CIF";
            hoja.Cell(1, 3).Value = "Estado documental";
            hoja.Cell(1, 4).Value = "% cumplimiento";
            hoja.Cell(1, 5).Value = "Creado";
            hoja.Row(1).Style.Font.Bold = true;
            hoja.Column(4).Style.NumberFormat.Format = "0%";

            // Pagina en lotes en vez de TamanoPagina: int.MaxValue (P2.7):
            // no materializa toda la tabla de empresas del tenant de golpe.
            var fila = 2;
            await foreach (var empresa in PaginadorExportacion.PaginarAsync((pagina, tamanoPagina) =>
                mediator.Send(
                    new ObtenerEmpresasQuery(Busqueda: null, Pagina: pagina, TamanoPagina: tamanoPagina),
                    cancellationToken)))
            {
                hoja.Cell(fila, 1).Value = empresa.RazonSocial;
                hoja.Cell(fila, 2).Value = empresa.Cif;
                hoja.Cell(fila, 3).Value = EstadoDocumentoUi.TextoDocumental(empresa.EstadoDocumental);
                if (empresa.CumplimientoPorcentaje is not null)
                    hoja.Cell(fila, 4).Value = empresa.CumplimientoPorcentaje.Value / 100.0;
                hoja.Cell(fila, 5).Value = empresa.CreadoEnUtc;
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
                "empresas.xlsx");
        });

        return endpoints;
    }
}
