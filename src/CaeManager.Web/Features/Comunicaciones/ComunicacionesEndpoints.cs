using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerAdjuntoParaDescarga;
using MediatR;

namespace CaeManager.Web.Features.Comunicaciones;

/// <summary>
/// Sirve el contenido de un adjunto de correo vía un endpoint autenticado —
/// mismo criterio que <c>DocumentosEndpoints</c>: nunca como archivo
/// estático público, y siempre resolviendo el alcance antes de abrir el
/// archivo (ver <see cref="ObtenerAdjuntoParaDescargaQuery"/>).
/// </summary>
public static class ComunicacionesEndpoints
{
    public static IEndpointRouteBuilder MapComunicacionesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/comunicaciones/adjuntos/{id:guid}/archivo", async (
            Guid id, IMediator mediator, IFileStorageService almacenamiento, CancellationToken cancellationToken) =>
        {
            var adjunto = await mediator.Send(new ObtenerAdjuntoParaDescargaQuery(id), cancellationToken);
            if (adjunto is null)
                return Results.NotFound();

            var flujo = await almacenamiento.AbrirAsync(adjunto.ArchivoUrl, cancellationToken);
            return Results.File(flujo, adjunto.TipoContenido, adjunto.NombreArchivo);
        });
        // Sin .RequireAuthorization() explícito: el FallbackPolicy global de
        // Program.cs (RequireAuthenticatedUser) ya lo cubre, mismo criterio
        // que DocumentosEndpoints.

        return endpoints;
    }
}
