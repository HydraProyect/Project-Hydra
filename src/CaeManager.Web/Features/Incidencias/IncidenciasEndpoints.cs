using CaeManager.Application.Incidencias.Queries.ObtenerIncidencias;
using CaeManager.Domain.Incidencias;
using CaeManager.Web.Exportacion;
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
            using var libro = new XLWorkbook();
            var hoja = libro.Worksheets.Add("Incidencias");

            hoja.Cell(1, 1).Value = "Centro";
            hoja.Cell(1, 2).Value = "Trabajador";
            hoja.Cell(1, 3).Value = "Tipo";
            hoja.Cell(1, 4).Value = "Gravedad";
            hoja.Cell(1, 5).Value = "Fecha de ocurrencia";
            hoja.Cell(1, 6).Value = "Estado";
            hoja.Row(1).Style.Font.Bold = true;

            // Pagina en lotes en vez de TamanoPagina: int.MaxValue (P2.7):
            // no materializa toda la tabla de incidencias del tenant de golpe.
            var fila = 2;
            await foreach (var incidencia in PaginadorExportacion.PaginarAsync((pagina, tamanoPagina) =>
                mediator.Send(
                    new ObtenerIncidenciasQuery(Busqueda: null, SoloSinResolver: false, Pagina: pagina, TamanoPagina: tamanoPagina),
                    cancellationToken)))
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

            // Se escribe directo en el stream que consume la respuesta HTTP
            // (Results.File lo cierra) en vez de bufferear en un MemoryStream
            // y duplicarlo otra vez con ToArray().
            var stream = new MemoryStream();
            libro.SaveAs(stream);
            stream.Position = 0;

            return Results.File(
                stream,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "incidencias.xlsx");
        })
        // Mismos roles que /incidencias (Incidencias.razor), que excluye Cliente
        // — mismo patrón que /clientes/exportar.xlsx. Sin esto, restringir la
        // página dejaba este endpoint como vía de bypass para descargar el
        // Excel completo de incidencias del tenant con cualquier rol
        // autenticado, Cliente incluido. ObtenerIncidenciasQuery ya aplica
        // IAlcanceDatosService, así que un GestorCae exporta su propia
        // cartera, no la de todos.
        .RequireAuthorization(policy => policy.RequireRole(
            CaeManager.Infrastructure.Identity.Roles.Administrador,
            CaeManager.Infrastructure.Identity.Roles.DireccionCae,
            CaeManager.Infrastructure.Identity.Roles.CoordinadorCae,
            CaeManager.Infrastructure.Identity.Roles.GestorCae,
            CaeManager.Infrastructure.Identity.Roles.Consulta));

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
