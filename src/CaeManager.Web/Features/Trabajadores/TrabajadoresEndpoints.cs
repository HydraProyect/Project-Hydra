using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Web.Exportacion;
using CaeManager.Web.Features.Documentos;
using ClosedXML.Excel;
using MediatR;

namespace CaeManager.Web.Features.Trabajadores;

/// <summary>Mismo patrón de referencia que ClientesEndpoints — ver comentario allí.</summary>
public static class TrabajadoresEndpoints
{
    public static IEndpointRouteBuilder MapTrabajadoresEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/trabajadores/exportar.xlsx", async (IMediator mediator, CancellationToken cancellationToken) =>
        {
            using var libro = new XLWorkbook();
            var hoja = libro.Worksheets.Add("Trabajadores");

            hoja.Cell(1, 1).Value = "Apellidos";
            hoja.Cell(1, 2).Value = "Nombre";
            hoja.Cell(1, 3).Value = "DNI";
            hoja.Cell(1, 4).Value = "Empresa/Subcontrata";
            hoja.Cell(1, 5).Value = "Documentación";
            hoja.Row(1).Style.Font.Bold = true;

            var fila = 2;
            await foreach (var trabajador in PaginadorExportacion.PaginarAsync((pagina, tamanoPagina) =>
                mediator.Send(
                    new ObtenerTrabajadoresQuery(Busqueda: null, Pagina: pagina, TamanoPagina: tamanoPagina),
                    cancellationToken)))
            {
                hoja.Cell(fila, 1).Value = trabajador.Apellidos;
                hoja.Cell(fila, 2).Value = trabajador.Nombre;
                hoja.Cell(fila, 3).Value = trabajador.Dni;
                hoja.Cell(fila, 4).Value = trabajador.EmpleadorNombre;
                hoja.Cell(fila, 5).Value = EstadoDocumentoUi.TextoDocumental(trabajador.EstadoDocumental);
                fila++;
            }

            hoja.Columns().AdjustToContents();

            var stream = new MemoryStream();
            libro.SaveAs(stream);
            stream.Position = 0;

            return Results.File(
                stream,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "trabajadores.xlsx");
        });

        return endpoints;
    }
}
