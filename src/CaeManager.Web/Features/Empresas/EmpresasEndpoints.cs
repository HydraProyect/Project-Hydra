using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
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
            var resultado = await mediator.Send(
                new ObtenerEmpresasQuery(Busqueda: null, Pagina: 1, TamanoPagina: int.MaxValue),
                cancellationToken);

            using var libro = new XLWorkbook();
            var hoja = libro.Worksheets.Add("Empresas");

            hoja.Cell(1, 1).Value = "Razón social";
            hoja.Cell(1, 2).Value = "CIF";
            hoja.Cell(1, 3).Value = "Estado documental";
            hoja.Cell(1, 4).Value = "% cumplimiento";
            hoja.Cell(1, 5).Value = "Creado";
            hoja.Row(1).Style.Font.Bold = true;

            var fila = 2;
            foreach (var empresa in resultado.Elementos)
            {
                hoja.Cell(fila, 1).Value = empresa.RazonSocial;
                hoja.Cell(fila, 2).Value = empresa.Cif;
                hoja.Cell(fila, 3).Value = EstadoDocumentoUi.TextoDocumental(empresa.EstadoDocumental);
                if (empresa.CumplimientoPorcentaje is not null)
                    hoja.Cell(fila, 4).Value = empresa.CumplimientoPorcentaje.Value / 100.0;
                hoja.Cell(fila, 5).Value = empresa.CreadoEnUtc;
                fila++;
            }

            hoja.Column(4).Style.NumberFormat.Format = "0%";
            hoja.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            libro.SaveAs(stream);

            return Results.File(
                stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "empresas.xlsx");
        });

        return endpoints;
    }
}
