using CaeManager.Application.Common;
using CaeManager.Application.Subcontratas.Queries.ObtenerEvidenciaVerificacionParaDescarga;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Subcontratas;
using CaeManager.Web.Exportacion;
using CaeManager.Web.Features.Subcontratas.Recursos;
using ClosedXML.Excel;
using MediatR;
using Microsoft.Extensions.Localization;

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
        endpoints.MapGet("/subcontratas/verificaciones/{id:guid}/evidencia", ServirEvidenciaAsync);

        // Mismo patrón de referencia que ClientesEndpoints.
        endpoints.MapGet("/subcontratas/exportar.xlsx", async (
            IMediator mediator, IStringLocalizer<TextosSubcontratas> textos, CancellationToken cancellationToken,
            string? q = null, string? nivel = null) =>
        {
            using var libro = new XLWorkbook();
            var hoja = libro.Worksheets.Add("Subcontratas");

            hoja.Cell(1, 1).Value = textos["ExcelColumnaRazonSocial"].Value;
            hoja.Cell(1, 2).Value = "CIF";
            hoja.Cell(1, 3).Value = textos["ExcelColumnaNivelServicio"].Value;
            hoja.Cell(1, 4).Value = textos["ExcelColumnaCumplimiento"].Value;
            hoja.Cell(1, 5).Value = textos["ExcelColumnaTotalVencidas"].Value;
            hoja.Cell(1, 6).Value = textos["ExcelColumnaTotalProximas"].Value;
            hoja.Row(1).Style.Font.Bold = true;

            var fila = 2;
            await foreach (var subcontrata in PaginadorExportacion.PaginarAsync((pagina, tamanoPagina) =>
                mediator.Send(
                    new ObtenerSubcontratasQuery(
                        Busqueda: string.IsNullOrWhiteSpace(q) ? null : q,
                        Pagina: pagina,
                        TamanoPagina: tamanoPagina,
                        NivelServicio: Enum.TryParse<NivelServicioSubcontrata>(nivel, out var nivelServicio) ? nivelServicio : null),
                    cancellationToken)))
            {
                hoja.Cell(fila, 1).Value = subcontrata.RazonSocial;
                hoja.Cell(fila, 2).Value = subcontrata.Cif;
                hoja.Cell(fila, 3).Value = EstadoSupervisionUi.TextoNivel(textos, subcontrata.NivelServicio);
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
    /// Manejador de <c>GET /subcontratas/verificaciones/{id}/evidencia</c>. Método con nombre, y no
    /// una lambda, para poder probarlo sin levantar el host (mismo criterio que
    /// <c>DocumentosEndpoints.ServirArchivoAsync</c>).
    /// </summary>
    public static async Task<IResult> ServirEvidenciaAsync(
        Guid id, IMediator mediator, IFileStorageService almacenamiento,
        IRegistroAccesoDocumentoSensibleService registroAcceso, ILoggerFactory fabricaRegistro,
        CancellationToken cancellationToken)
    {
        var evidencia = await mediator.Send(new ObtenerEvidenciaVerificacionParaDescargaQuery(id), cancellationToken);
        if (evidencia is null)
            return Results.NotFound();

        // DEC-36 (REC-099): a diferencia de un adjunto de correo o una
        // plantilla en blanco, la evidencia de VerificacionExternaSubcontrata
        // SÍ tiene un TipoDocumentoId real (Codex lo detectó antes de
        // abrir la PR — la exclusión original asumía lo contrario).
        // Se abre primero y se registra después, como en DocumentosEndpoints:
        // si el fichero no está, no queda el registro de un acceso que no
        // entregó contenido.
        Stream flujo;
        try
        {
            flujo = await almacenamiento.AbrirAsync(evidencia.ArchivoRuta, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            // La verificación dice tener evidencia, pero el almacén no la tiene: 404 y un aviso, y
            // no la excepción sin controlar. El aviso lleva el Id de la verificación y no la
            // excepción ni la clave del fichero: la ruta del almacén no sale ni en la respuesta ni
            // en el registro.
            fabricaRegistro.CreateLogger(typeof(SubcontratasEndpoints).FullName!).LogWarning(
                "La verificación externa {VerificacionId} tiene evidencia registrada pero su fichero no está en el almacén; se responde 404.",
                id);
            return Results.NotFound();
        }

        await registroAcceso.RegistrarSiSensibleAsync(id, evidencia.TipoDocumentoId, TipoAccesoDocumentoSensible.Apertura, cancellationToken);
        return Results.File(flujo, TipoContenidoDe(evidencia.NombreArchivo), evidencia.NombreArchivo, enableRangeProcessing: true);
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
