using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.ConfirmarSubidaDesdeExtension;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Autenticacion;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using MediatR;
using System.Text.Json;

namespace CaeManager.Web.Features.Extension;

/// <summary>
/// «Lo he enviado» y su «Deshacer» desde la extensión de navegador (incremento E1 del plan de la extensión; antes,
/// Incremento 3 del MVP1 de Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md § 14; Incremento 1 en
/// <see cref="ExtensionTokenEndpoints"/>, Incremento 2 en <see cref="AcreditacionesPendientesEndpoints"/>).
///
/// <para>
/// <c>POST /extension/acreditaciones/{id}/subida</c> ya no llega al poner el archivo en el formulario: la extensión lo
/// llama cuando el Gestor CAE confirma «Lo he enviado». Delega en <see cref="ConfirmarSubidaDesdeExtensionCommand"/>,
/// que resuelve el alcance por cartera, la regla de la rechazada y el proveedor activo, y deja un cambio de estado con
/// origen Extensión. Responde 200 con <c>{ cambioId, version }</c>, lo que pide el «Deshacer»; una extensión 0.4.x, que
/// solo mira <c>ok</c>, sigue funcionando.
/// </para>
/// <para>
/// <c>POST /extension/acreditaciones/{id}/subida/deshacer</c> con <c>{ cambioId, version }</c> delega en
/// <see cref="DeshacerSubidaDesdeExtensionCommand"/> y responde 204.
/// </para>
/// </summary>
public static class MarcarAcreditacionSubidaEndpoints
{
    /// <summary>Lo que el «Deshacer» de la extensión devuelve tal cual lo recibió de la confirmación.</summary>
    public sealed record DeshacerSubidaPeticion(Guid CambioId, Guid Version);

    /// <param name="DominioPortal">El host de la pestaña donde la extensión puso el documento (p. ej. «app.dokify.net»).</param>
    public sealed record ConfirmarSubidaPeticion(string? DominioPortal);

    private static async Task<ConfirmarSubidaPeticion?> LeerPeticionAsync(HttpRequest solicitud, CancellationToken cancellationToken)
    {
        if (!solicitud.HasJsonContentType()) return null;

        try
        {
            return await solicitud.ReadFromJsonAsync<ConfirmarSubidaPeticion>(cancellationToken);
        }
        catch (JsonException)
        {
            return null; // cuerpo mal formado: se trata como «sin portal declarado».
        }
    }

    public static IEndpointRouteBuilder MapMarcarAcreditacionSubidaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/extension/acreditaciones/{id:guid}/subida", async (
            Guid id, HttpRequest solicitud, IMediator mediator, CancellationToken cancellationToken) =>
        {
            // Un id vacío no es ninguna acreditación: el mismo 404 que "no existe", sin llegar al comando.
            if (id == Guid.Empty)
                return Results.NotFound();
            // El cuerpo se lee a mano, y no como parámetro enlazado, para que el filtro antifalsificación de abajo
            // siga siendo lo primero que responde (un parámetro de cuerpo respondería 415 a un POST de formulario antes
            // de llegar al filtro) y para que una petición sin cuerpo o sin JSON siga llegando al comando, que es quien
            // dice «no declaraste el portal».
            var peticion = await LeerPeticionAsync(solicitud, cancellationToken);
            var resultado = await mediator.Send(
                new ConfirmarSubidaDesdeExtensionCommand(id, peticion?.DominioPortal), cancellationToken);
            return resultado.EsFallido
                ? Fallo(resultado.Error)
                : Results.Ok(new { cambioId = resultado.Valor.CambioId, version = resultado.Valor.Version });
        })
        .ConEntradaDeExtension();

        endpoints.MapPost("/extension/acreditaciones/{id:guid}/subida/deshacer", async (
            Guid id, DeshacerSubidaPeticion peticion, IMediator mediator, CancellationToken cancellationToken) =>
        {
            if (id == Guid.Empty)
                return Results.NotFound();
            // Sin cambio o sin versión la petición está mal formada (400): ni es un conflicto ni algo que no existe. La
            // extensión siempre reenvía lo que recibió de la confirmación.
            if (peticion is null || peticion.CambioId == Guid.Empty || peticion.Version == Guid.Empty)
                return Results.BadRequest();
            var resultado = await mediator.Send(
                new DeshacerSubidaDesdeExtensionCommand(id, peticion.CambioId, peticion.Version), cancellationToken);
            return resultado.EsFallido ? Fallo(resultado.Error) : Results.NoContent();
        })
        .ConEntradaDeExtension();

        return endpoints;
    }

    // "Acreditacion.NoEncontrada" cubre a la vez "no existe" y "fuera de cartera" -- a propósito, para no filtrar por
    // enumeración cuál de las dos es (mismo criterio que el resto de Commands de Documentos). Lo demás es un 409 con su
    // detalle: la extensión necesita mostrar "conector desactivado" o "rechazada sin versión nueva", no un 404 genérico
    // que el Gestor CAE confundiría con un documento inexistente.
    private static IResult Fallo(Error error)
    {
        // Un portal que no es el de la Plataforma CAE del canal también es un 409 (existe, pero no se puede anotar ahí);
        // sin portal declarado es una petición mal formada (400).
        var statusCode = error.Codigo switch
        {
            ConfirmarSubidaDesdeExtensionCommandHandler.CodigoConectorInactivo
                or MarcarAcreditacionSubidaCommandHandler.CodigoRechazadaSinVersionNueva
                or DeshacerSubidaDesdeExtensionCommandHandler.CodigoYaNoSePuedeDeshacer
                or ConfirmarSubidaDesdeExtensionCommandHandler.CodigoPortalAjeno
                or ConcurrenciaOptimista.CodigoConflicto => StatusCodes.Status409Conflict,
            ConfirmarSubidaDesdeExtensionCommandHandler.CodigoPortalNoDeclarado => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status404NotFound,
        };
        return Results.Problem(error.Mensaje, statusCode: statusCode,
            title: error.Codigo == ConfirmarSubidaDesdeExtensionCommandHandler.CodigoPortalAjeno
                ? error.Codigo : null);
    }

    private static RouteHandlerBuilder ConEntradaDeExtension(this RouteHandlerBuilder ruta) => ruta
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
}
