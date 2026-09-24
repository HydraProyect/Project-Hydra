using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Exportacion;
using CaeManager.Web.Features.Documentos;
using ClosedXML.Excel;
using MediatR;

namespace CaeManager.Web.Features.Trabajadores;

/// <summary>
/// Fila de la exportación de Trabajadores. No lleva DNI a propósito: el tipo
/// no tiene dónde guardarlo, así que el escritor del libro no puede volver a
/// pintarlo por descuido. Decisión S4 (2026-09-24, provisional hasta que se
/// resuelva Q23 del mapa de tratamientos, ficha A4): la exportación masiva
/// del DNI de todo el ámbito visible, sin rastro de acceso, queda retirada;
/// el DNI se sigue consultando en la ficha del Trabajador.
/// </summary>
public sealed record FilaExportacionTrabajador(
    string Apellidos, string Nombre, string EmpleadorNombre, EstadoDocumento? EstadoDocumental)
{
    public static FilaExportacionTrabajador Desde(TrabajadorListaDto trabajador) =>
        new(trabajador.Apellidos, trabajador.Nombre, trabajador.EmpleadorNombre, trabajador.EstadoDocumental);
}

/// <summary>Mismo patrón de referencia que ClientesEndpoints — ver comentario allí.</summary>
public static class TrabajadoresEndpoints
{
    public static IEndpointRouteBuilder MapTrabajadoresEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/trabajadores/exportar.xlsx", async (IMediator mediator, CancellationToken cancellationToken) =>
        {
            var trabajadores = PaginadorExportacion.PaginarAsync((pagina, tamanoPagina) =>
                mediator.Send(
                    new ObtenerTrabajadoresQuery(Busqueda: null, Pagina: pagina, TamanoPagina: tamanoPagina),
                    cancellationToken));

            var stream = await GenerarLibroAsync(trabajadores, cancellationToken);

            return Results.File(
                stream,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "trabajadores.xlsx");
        });

        return endpoints;
    }

    /// <summary>
    /// Escribe el libro a partir de lo que devuelve la query del listado,
    /// pasando cada Trabajador por <see cref="FilaExportacionTrabajador"/>
    /// antes de tocar la hoja. Público para que el test mida el xlsx real sin
    /// levantar la aplicación (el proyecto no expone WebApplicationFactory).
    /// </summary>
    public static async Task<MemoryStream> GenerarLibroAsync(
        IAsyncEnumerable<TrabajadorListaDto> trabajadores, CancellationToken cancellationToken = default)
    {
        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add("Trabajadores");

        hoja.Cell(1, 1).Value = "Apellidos";
        hoja.Cell(1, 2).Value = "Nombre";
        hoja.Cell(1, 3).Value = "Empresa/Subcontrata";
        hoja.Cell(1, 4).Value = "Documentación";
        hoja.Row(1).Style.Font.Bold = true;

        var fila = 2;
        await foreach (var trabajador in trabajadores.WithCancellation(cancellationToken))
        {
            var exportada = FilaExportacionTrabajador.Desde(trabajador);
            hoja.Cell(fila, 1).Value = exportada.Apellidos;
            hoja.Cell(fila, 2).Value = exportada.Nombre;
            hoja.Cell(fila, 3).Value = exportada.EmpleadorNombre;
            hoja.Cell(fila, 4).Value = EstadoDocumentoUi.TextoDocumental(exportada.EstadoDocumental);
            fila++;
        }

        hoja.Columns().AdjustToContents();

        var stream = new MemoryStream();
        libro.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }
}
