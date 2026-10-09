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
        endpoints.MapGet("/comunicaciones/adjuntos/{id:guid}/archivo", ServirAdjuntoAsync);
        // Sin .RequireAuthorization() explícito: el FallbackPolicy global de
        // Program.cs (RequireAuthenticatedUser) ya lo cubre, mismo criterio
        // que DocumentosEndpoints.

        return endpoints;
    }

    /// <summary>
    /// Manejador de <c>GET /comunicaciones/adjuntos/{id}/archivo</c>. Método con nombre, y no una
    /// lambda, para poder probarlo sin levantar el host (mismo criterio que
    /// <c>DocumentosEndpoints.ServirArchivoAsync</c>).
    /// </summary>
    public static async Task<IResult> ServirAdjuntoAsync(
        Guid id, IMediator mediator, IFileStorageService almacenamiento, ILoggerFactory fabricaRegistro,
        CancellationToken cancellationToken)
    {
        var adjunto = await mediator.Send(new ObtenerAdjuntoParaDescargaQuery(id), cancellationToken);
        if (adjunto is null)
            return Results.NotFound();

        // No pasa por IRegistroAccesoDocumentoSensibleService (DEC-36,
        // HO-099-01 § 6-7): un AdjuntoMensaje es contenido de correo, sin
        // TipoDocumentoId — no hay de dónde sacar la categoría del punto
        // único de REC-132 sin inventar una fuente distinta, que es
        // justo lo que esa decisión prohíbe.
        Stream flujo;
        try
        {
            flujo = await almacenamiento.AbrirAsync(adjunto.ArchivoUrl, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            // El adjunto existe para quien pide, pero el almacén no tiene su fichero: 404 y un
            // aviso, como en DocumentosEndpoints, y no la excepción sin controlar. El aviso lleva el
            // Id del adjunto y no la excepción ni la clave del fichero: la ruta del almacén no sale
            // ni en la respuesta ni en el registro.
            fabricaRegistro.CreateLogger(typeof(ComunicacionesEndpoints).FullName!).LogWarning(
                "El adjunto de mensaje {AdjuntoId} está registrado pero su fichero no está en el almacén; se responde 404.",
                id);
            return Results.NotFound();
        }

        return Results.File(flujo, adjunto.TipoContenido, adjunto.NombreArchivo, enableRangeProcessing: true);
    }
}
