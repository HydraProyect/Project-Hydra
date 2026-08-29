using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Importacion;
using CaeManager.Web.Exportacion;
using ClosedXML.Excel;
using MediatR;

namespace CaeManager.Web.Features.Documentos;

/// <summary>
/// Sirve el PDF adjunto de un Documento vía un endpoint autenticado — nunca
/// como archivo estático público, precisamente porque IFileStorageService
/// guarda fuera de wwwroot (ver ARCHITECTURE.md, "Archivos").
/// </summary>
public static class DocumentosEndpoints
{
    public static IEndpointRouteBuilder MapDocumentosEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/documentos/{id:guid}/archivo", async (
            Guid id, IMediator mediator, IFileStorageService almacenamiento, CancellationToken cancellationToken) =>
        {
            var documento = await mediator.Send(new ObtenerDocumentoPorIdQuery(id), cancellationToken);
            if (documento?.ArchivoUrl is null)
                return Results.NotFound();

            var flujo = await almacenamiento.AbrirAsync(documento.ArchivoUrl, cancellationToken);
            return Results.File(flujo, "application/pdf", $"{documento.TipoDocumentoNombre}.pdf");
        });

        endpoints.MapGet("/documentos/plantilla.xlsx", (IPlantillaDocumentosService servicio) =>
            Results.File(
                servicio.GenerarPlantilla(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "plantilla-documentos.xlsx"));

        // Mismo patrón de referencia que ClientesEndpoints. Sin columna de
        // Plataformas/Acreditaciones: ObtenerDocumentosQueryHandler la
        // resuelve con una segunda pasada fuera de la página, y repetirla
        // por cada lote de PaginadorExportacion sería un N+1.
        endpoints.MapGet("/documentos/exportar.xlsx", async (IMediator mediator, CancellationToken cancellationToken) =>
        {
            using var libro = new XLWorkbook();
            var hoja = libro.Worksheets.Add("Documentos");

            hoja.Cell(1, 1).Value = "Propietario";
            hoja.Cell(1, 2).Value = "Ámbito";
            hoja.Cell(1, 3).Value = "Tipo de documento";
            hoja.Cell(1, 4).Value = "Emisión";
            hoja.Cell(1, 5).Value = "Vencimiento";
            hoja.Cell(1, 6).Value = "Estado";
            hoja.Row(1).Style.Font.Bold = true;

            var fila = 2;
            await foreach (var documento in PaginadorExportacion.PaginarAsync((pagina, tamanoPagina) =>
                mediator.Send(
                    new ObtenerDocumentosQuery(TrabajadorId: null, Ambito: null, Busqueda: null, Pagina: pagina, TamanoPagina: tamanoPagina),
                    cancellationToken)))
            {
                hoja.Cell(fila, 1).Value = documento.PropietarioNombre;
                hoja.Cell(fila, 2).Value = documento.Ambito.ToString();
                hoja.Cell(fila, 3).Value = documento.TipoDocumentoNombre;
                hoja.Cell(fila, 4).Value = documento.FechaEmision.ToDateTime(TimeOnly.MinValue);
                if (documento.FechaVencimiento is not null)
                    hoja.Cell(fila, 5).Value = documento.FechaVencimiento.Value.ToDateTime(TimeOnly.MinValue);
                hoja.Cell(fila, 6).Value = EstadoDocumentoUi.Texto(documento.Estado);
                fila++;
            }

            hoja.Columns().AdjustToContents();

            var stream = new MemoryStream();
            libro.SaveAs(stream);
            stream.Position = 0;

            return Results.File(
                stream,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "documentos.xlsx");
        });

        return endpoints;
    }
}
