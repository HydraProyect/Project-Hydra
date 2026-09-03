using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerFirmaGuardadaUsuario;
using CaeManager.Application.Documentos.Queries.ObtenerSelloEmpresa;
using MediatR;

namespace CaeManager.Web.Features.Documentos;

/// <summary>
/// Sirve las imágenes de la firma guardada del usuario y el sello guardado
/// de una Empresa vía endpoints autenticados — mismo criterio que
/// DocumentosEndpoints (IFileStorageService guarda fuera de wwwroot).
/// </summary>
public static class FirmasGuardadasEndpoints
{
    public static IEndpointRouteBuilder MapFirmasGuardadasEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/mi-firma/archivo", async (
            IMediator mediator, IFileStorageService almacenamiento, CancellationToken cancellationToken) =>
        {
            var firma = await mediator.Send(new ObtenerFirmaGuardadaUsuarioQuery(), cancellationToken);
            if (firma is null)
                return Results.NotFound();

            // No pasa por IRegistroAccesoDocumentoSensibleService (DEC-36,
            // HO-099-01 § 6-7): FirmaGuardadaUsuario es una imagen de firma,
            // sin TipoDocumentoId — no es un Documento del catálogo.
            var flujo = await almacenamiento.AbrirAsync(firma.ImagenUrl, cancellationToken);
            return Results.File(flujo, "image/png", enableRangeProcessing: true);
        });

        endpoints.MapGet("/empresas/{id:guid}/sello/archivo", async (
            Guid id, IMediator mediator, IFileStorageService almacenamiento, CancellationToken cancellationToken) =>
        {
            var sello = await mediator.Send(new ObtenerSelloEmpresaQuery(id), cancellationToken);
            if (sello is null)
                return Results.NotFound();

            // Mismo motivo que /mi-firma/archivo arriba: SelloEmpresa es una
            // imagen de sello, no un Documento clasificable por TipoDocumento.
            var flujo = await almacenamiento.AbrirAsync(sello.ImagenUrl, cancellationToken);
            return Results.File(flujo, "image/png", enableRangeProcessing: true);
        });

        return endpoints;
    }
}
