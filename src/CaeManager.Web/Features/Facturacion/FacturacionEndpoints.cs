using CaeManager.Application.Facturacion.Queries.ObtenerResumenFacturacion;
using ClosedXML.Excel;
using MediatR;

namespace CaeManager.Web.Features.Facturacion;

/// <summary>
/// Mismo patrón que ClientesEndpoints.cs (docs/ux-audit/11-reportes-facturacion.md H2)
/// — exporta el resumen exacto que la pestaña "Resumen mensual" ya calculó
/// para el cliente/año/mes seleccionados, no "todo".
/// </summary>
public static class FacturacionEndpoints
{
    public static IEndpointRouteBuilder MapFacturacionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/facturacion/resumen.xlsx", async (
            Guid clienteId, int anyo, int mes, IMediator mediator, CancellationToken cancellationToken) =>
        {
            var resumen = await mediator.Send(new ObtenerResumenFacturacionQuery(clienteId, anyo, mes), cancellationToken);
            if (resumen is null)
                return Results.NotFound();

            using var libro = new XLWorkbook();
            var hoja = libro.Worksheets.Add("Resumen");

            hoja.Cell(1, 1).Value = $"Resumen — {resumen.ClienteNombre} — {Pages.Facturacion.NombreMes(mes)} {anyo}";
            // Referencia calificada: el namespace CaeManager.Web.Features.Facturacion
            // y la página Facturacion comparten nombre — "Pages.Facturacion" desambigua.
            hoja.Range(1, 1, 1, 4).Merge().Style.Font.Bold = true;

            hoja.Cell(3, 1).Value = "Concepto";
            hoja.Cell(3, 2).Value = "Unidades";
            hoja.Cell(3, 3).Value = "Precio unitario";
            hoja.Cell(3, 4).Value = "Subtotal";
            hoja.Row(3).Style.Font.Bold = true;

            var fila = 4;
            foreach (var linea in resumen.Lineas)
            {
                hoja.Cell(fila, 1).Value = linea.ConceptoNombre;
                hoja.Cell(fila, 2).Value = linea.Unidades;
                hoja.Cell(fila, 3).Value = linea.PrecioUnitario;
                hoja.Cell(fila, 4).Value = linea.Subtotal;
                fila++;
            }

            hoja.Cell(fila, 1).Value = "Total estimado";
            hoja.Cell(fila, 1).Style.Font.Bold = true;
            hoja.Cell(fila, 4).Value = resumen.TotalEstimado;
            hoja.Cell(fila, 4).Style.Font.Bold = true;

            hoja.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            libro.SaveAs(stream);

            return Results.File(
                stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"facturacion-{resumen.ClienteNombre}-{anyo}-{mes:00}.xlsx");
        }).RequireAuthorization(policy => policy.RequireRole(
            CaeManager.Infrastructure.Identity.Roles.Administrador, CaeManager.Infrastructure.Identity.Roles.DireccionCae));

        return endpoints;
    }
}
