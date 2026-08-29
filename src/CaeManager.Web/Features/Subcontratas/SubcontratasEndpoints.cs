using CaeManager.Application.Common;
using CaeManager.Application.Subcontratas.Queries.ObtenerEvidenciaVerificacionParaDescarga;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Web.Exportacion;
using ClosedXML.Excel;
using MediatR;

namespace CaeManager.Web.Features.Subcontratas;

/// <summary>
/// Sirve la evidencia adjunta de una verificación externa (ADR-005 § 2.3) —
/// mismo criterio que <c>ComunicacionesEndpoints</c>: endpoint autenticado
/// (FallbackPolicy global) que resuelve el alcance antes de abrir el archivo.
/// </summary>
public static class SubcontratasEndpoints
{
    public static IEndpointRouteBuilder MapSubcontratasEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/subcontratas/verificaciones/{id:guid}/evidencia", async (
            Guid id, IMediator mediator, IFileStorageService almacenamiento, CancellationToken cancellationToken) =>
        {
            var evidencia = await mediator.Send(new ObtenerEvidenciaVerificacionParaDescargaQuery(id), cancellationToken);
            if (evidencia is null)
                return Results.NotFound();

            var flujo = await almacenamiento.AbrirAsync(evidencia.ArchivoRuta, cancellationToken);
            return Results.File(flujo, TipoContenidoDe(evidencia.NombreArchivo), evidencia.NombreArchivo);
        });

        // Mismo patrón de referencia que ClientesEndpoints.
        endpoints.MapGet("/subcontratas/exportar.xlsx", async (IMediator mediator, CancellationToken cancellationToken) =>
        {
            using var libro = new XLWorkbook();
            var hoja = libro.Worksheets.Add("Subcontratas");

            hoja.Cell(1, 1).Value = "Razón social";
            hoja.Cell(1, 2).Value = "CIF";
            hoja.Cell(1, 3).Value = "Nivel de servicio";
            hoja.Cell(1, 4).Value = "% Cumplimiento";
            hoja.Cell(1, 5).Value = "Total vencidas";
            hoja.Cell(1, 6).Value = "Total próximas";
            hoja.Row(1).Style.Font.Bold = true;

            var fila = 2;
            await foreach (var subcontrata in PaginadorExportacion.PaginarAsync((pagina, tamanoPagina) =>
                mediator.Send(
                    new ObtenerSubcontratasQuery(Busqueda: null, Pagina: pagina, TamanoPagina: tamanoPagina),
                    cancellationToken)))
            {
                hoja.Cell(fila, 1).Value = subcontrata.RazonSocial;
                hoja.Cell(fila, 2).Value = subcontrata.Cif;
                hoja.Cell(fila, 3).Value = EstadoSupervisionUi.TextoNivel(subcontrata.NivelServicio);
                if (subcontrata.CumplimientoPorcentaje is not null)
                    hoja.Cell(fila, 4).Value = subcontrata.CumplimientoPorcentaje.Value;
                hoja.Cell(fila, 5).Value = subcontrata.Recuentos.TotalVencidas;
                hoja.Cell(fila, 6).Value = subcontrata.Recuentos.TotalProximas;
                fila++;
            }

            hoja.Columns().AdjustToContents();

            var stream = new MemoryStream();
            libro.SaveAs(stream);
            stream.Position = 0;

            return Results.File(
                stream,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "subcontratas.xlsx");
        });

        return endpoints;
    }

    /// <summary>
    /// El tipo de contenido no se almacenó con la evidencia — se deriva de la
    /// extensión para que capturas y PDFs se abran en el navegador; cualquier
    /// otra cosa se descarga como binario.
    /// </summary>
    private static string TipoContenidoDe(string nombreArchivo) =>
        Path.GetExtension(nombreArchivo).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
}
