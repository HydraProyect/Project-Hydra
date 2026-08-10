using CaeManager.Application.Common;
using CaeManager.Application.Subcontratas.Queries.ObtenerEvidenciaVerificacionParaDescarga;
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
