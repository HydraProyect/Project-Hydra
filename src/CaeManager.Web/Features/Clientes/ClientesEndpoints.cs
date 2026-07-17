using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Importacion;
using ClosedXML.Excel;
using MediatR;

namespace CaeManager.Web.Features.Clientes;

/// <summary>
/// Exportación a Excel: patrón de referencia que el resto de módulos con
/// tabla replican (ver ROADMAP.md, Fase 1). Se sirve como endpoint aparte en
/// vez de generarse dentro del circuito de Blazor porque descargar un
/// archivo binario grande no encaja bien con SignalR.
/// </summary>
public static class ClientesEndpoints
{
    public static IEndpointRouteBuilder MapClientesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/clientes/exportar.xlsx", async (IMediator mediator, CancellationToken cancellationToken) =>
        {
            var resultado = await mediator.Send(
                new ObtenerClientesQuery(Busqueda: null, SoloCriticos: null, Pagina: 1, TamanoPagina: int.MaxValue),
                cancellationToken);

            using var libro = new XLWorkbook();
            var hoja = libro.Worksheets.Add("Clientes");

            hoja.Cell(1, 1).Value = "Razón social";
            hoja.Cell(1, 2).Value = "CIF";
            hoja.Cell(1, 3).Value = "Crítico";
            hoja.Cell(1, 4).Value = "Creado";
            hoja.Row(1).Style.Font.Bold = true;

            var fila = 2;
            foreach (var cliente in resultado.Elementos)
            {
                hoja.Cell(fila, 1).Value = cliente.RazonSocial;
                hoja.Cell(fila, 2).Value = cliente.Cif;
                hoja.Cell(fila, 3).Value = cliente.EsCritico ? "Sí" : "No";
                hoja.Cell(fila, 4).Value = cliente.CreadoEnUtc;
                fila++;
            }

            hoja.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            libro.SaveAs(stream);

            return Results.File(
                stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "clientes.xlsx");
        });

        endpoints.MapGet("/clientes/plantilla.xlsx", (IPlantillaClientesService servicio) =>
            Results.File(
                servicio.GenerarPlantilla(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "plantilla-clientes.xlsx"));

        endpoints.MapGet("/clientes/plantilla-combinada.xlsx", (IPlantillaCombinadaService servicio) =>
            Results.File(
                servicio.GenerarPlantilla(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "plantilla-combinada.xlsx"));

        return endpoints;
    }
}
