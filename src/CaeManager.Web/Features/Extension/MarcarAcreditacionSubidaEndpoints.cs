using CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;
using CaeManager.Infrastructure.Identity;
using MediatR;

namespace CaeManager.Web.Features.Extension;

/// <summary>
/// Incremento 3 del MVP1 de integración con plataformas CAE externas vía
/// extensión de navegador (ver ARQUITECTURA-INTEGRACIONES.md § 14 en el
/// repositorio de negocio; Incremento 1 en <see cref="ExtensionTokenEndpoints"/>,
/// Incremento 2 en <see cref="AcreditacionesPendientesEndpoints"/>).
///
/// <para>
/// No es un Command nuevo: envuelve <see cref="MarcarAcreditacionSubidaCommand"/>
/// -- ya conectado al botón "Marcar subido" de <c>PlataformaTab.razor</c> — sin
/// tocar su lógica. El propio Command ya resuelve el alcance por cartera
/// (carga el Documento dueño de la acreditación y comprueba
/// <c>IAlcanceDatosService.DocumentoVisibleAsync</c>), así que este endpoint no
/// necesita repetir esa comprobación ni conocer el Centro o el
/// CanalGestionDocumentalId que expuso el Incremento 2 — solo el AcreditacionId
/// que la extensión ya recibió de <c>/extension/acreditaciones-pendientes</c>.
/// </para>
/// </summary>
public static class MarcarAcreditacionSubidaEndpoints
{
    public static IEndpointRouteBuilder MapMarcarAcreditacionSubidaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/extension/acreditaciones/{id:guid}/subida", async (
            Guid id, IMediator mediator, CancellationToken cancellationToken) =>
        {
            // ExigirProveedorActivo: true — solo este llamante (la extensión),
            // nunca PlataformaTab.razor (ver el comentario del propio Command,
            // MVP2 § 14.5, kill switch remoto).
            var resultado = await mediator.Send(
                new MarcarAcreditacionSubidaCommand(id, ExigirProveedorActivo: true), cancellationToken);

            // "Acreditacion.NoEncontrada" y "Acreditacion.ConectorInactivo" son
            // hoy los códigos de fallo de este Command; el primero cubre a la
            // vez "no existe" y "fuera de cartera" -- a propósito, para no
            // filtrar por enumeración cuál de las dos es (mismo criterio que
            // el resto de Commands de Documentos). El segundo sí se distingue:
            // la extensión necesita mostrar "conector desactivado", no un 404
            // genérico que el gestor confundiría con un documento inexistente.
            if (!resultado.EsFallido) return Results.NoContent();

            var statusCode = resultado.Error.Codigo == "Acreditacion.ConectorInactivo"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status404NotFound;
            return Results.Problem(resultado.Error.Mensaje, statusCode: statusCode);
        })
        // Misma política que el resto de /extension/*: sesión interactiva o
        // token de extensión, nunca clave de API de tenant.
        .RequireAuthorization(Policies.SesionOExtension);

        return endpoints;
    }
}
