using CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Autenticacion;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using MediatR;
using System.Text.Json;

namespace CaeManager.Web.Features.Extension;

/// <summary>
/// Incremento 3 del MVP1 de integración con plataformas CAE externas vía
/// extensión de navegador (ver Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md § 14 en el
/// repositorio de negocio; Incremento 1 en <see cref="ExtensionTokenEndpoints"/>,
/// Incremento 2 en <see cref="AcreditacionesPendientesEndpoints"/>).
///
/// <para>
/// No es un Command nuevo: envuelve <see cref="MarcarAcreditacionSubidaCommand"/>
/// -- ya conectado al botón "Marcar subido" de <c>PlataformaTab.razor</c>. El
/// propio Command ya resuelve el alcance por cartera (carga el Documento dueño de
/// la acreditación y comprueba <c>IAlcanceDatosService.DocumentoVisibleAsync</c>),
/// así que este endpoint no necesita repetir esa comprobación ni conocer el Centro
/// o el CanalGestionDocumentalId que expuso el Incremento 2: recibe el
/// AcreditacionId que la extensión ya recibió de
/// <c>/extension/acreditaciones-pendientes</c> y el host del portal donde puso el
/// documento (<see cref="SubidaAcreditacionPeticion"/>), que el Command compara
/// con los dominios de la Plataforma CAE del canal.
/// </para>
/// </summary>
/// <param name="DominioPortal">El host de la pestaña donde la extensión puso el documento (p. ej. «app.dokify.net»).</param>
public sealed record SubidaAcreditacionPeticion(string? DominioPortal);

public static class MarcarAcreditacionSubidaEndpoints
{
    public static IEndpointRouteBuilder MapMarcarAcreditacionSubidaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/extension/acreditaciones/{id:guid}/subida", async (
            Guid id, HttpRequest solicitud, IMediator mediator, CancellationToken cancellationToken) =>
        {
            // El cuerpo se lee a mano, y no como parámetro enlazado, para que el
            // filtro antifalsificación de abajo siga siendo lo primero que responde
            // (un parámetro de cuerpo respondería 415 a un POST de formulario antes
            // de llegar al filtro) y para que una petición sin cuerpo o sin JSON
            // siga llegando al Command, que es quien dice «no declaraste el portal».
            var peticion = await LeerPeticionAsync(solicitud, cancellationToken);

            // ExigirProveedorActivo y ExigirPortalDelProveedor: true — solo este
            // llamante (la extensión), nunca PlataformaTab.razor (ver el comentario
            // del propio Command: kill switch remoto, MVP2 § 14.5, y que el portal
            // declarado sea el de la Plataforma CAE del canal).
            var resultado = await mediator.Send(
                new MarcarAcreditacionSubidaCommand(
                    id, ExigirProveedorActivo: true,
                    DominioPortal: peticion?.DominioPortal, ExigirPortalDelProveedor: true),
                cancellationToken);

            // "Acreditacion.NoEncontrada" y "Acreditacion.ConectorInactivo" son
            // hoy los códigos de fallo de este Command; el primero cubre a la
            // vez "no existe" y "fuera de cartera" -- a propósito, para no
            // filtrar por enumeración cuál de las dos es (mismo criterio que
            // el resto de Commands de Documentos). El segundo sí se distingue:
            // la extensión necesita mostrar "conector desactivado", no un 404
            // genérico que el gestor confundiría con un documento inexistente.
            if (!resultado.EsFallido) return Results.NoContent();

            // Una Rechazada tampoco es un 404: existe, pero sin versión nueva
            // no se puede dar por subida (MarcarAcreditacionSubidaCommand).
            // Un portal que no es el de la Plataforma CAE del canal también es un
            // 409 (existe, pero no se puede registrar ahí); sin portal declarado es
            // una petición mal formada (400).
            var statusCode = resultado.Error.Codigo switch
            {
                "Acreditacion.ConectorInactivo"
                    or MarcarAcreditacionSubidaCommandHandler.CodigoRechazadaSinVersionNueva
                    or MarcarAcreditacionSubidaCommandHandler.CodigoPortalAjeno => StatusCodes.Status409Conflict,
                MarcarAcreditacionSubidaCommandHandler.CodigoPortalNoDeclarado => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status404NotFound,
            };
            return Results.Problem(resultado.Error.Mensaje, statusCode: statusCode);
        })
        // Misma política que el resto de /extension/*: sesión interactiva o
        // token de extensión, nunca clave de API de tenant.
        .RequireAuthorization(Policies.SesionOExtension)
        .AddEndpointFilter(async (contexto, siguiente) =>
        {
            // Solo el esquema validado de extensión exime del token: una cabecera
            // arbitraria junto a la cookie no acredita una petición de la extensión.
            var extension = await contexto.HttpContext.AuthenticateAsync(ExtensionAuthenticationSchemeOptions.NombreEsquema);
            if (!extension.Succeeded)
            {
                try
                {
                    await contexto.HttpContext.RequestServices.GetRequiredService<IAntiforgery>()
                        .ValidateRequestAsync(contexto.HttpContext);
                }
                catch (AntiforgeryValidationException)
                {
                    return Results.BadRequest();
                }
            }
            return await siguiente(contexto);
        });

        return endpoints;
    }

    private static async Task<SubidaAcreditacionPeticion?> LeerPeticionAsync(HttpRequest solicitud, CancellationToken cancellationToken)
    {
        if (!solicitud.HasJsonContentType()) return null;

        try
        {
            return await solicitud.ReadFromJsonAsync<SubidaAcreditacionPeticion>(cancellationToken);
        }
        catch (JsonException)
        {
            return null; // cuerpo mal formado: se trata como «sin portal declarado».
        }
    }
}
