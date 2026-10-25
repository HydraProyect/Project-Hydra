using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.ConfirmarSubidaDesdeExtension;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Autenticacion;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using MediatR;

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

    public static IEndpointRouteBuilder MapMarcarAcreditacionSubidaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/extension/acreditaciones/{id:guid}/subida", async (
            Guid id, IMediator mediator, CancellationToken cancellationToken) =>
        {
            var resultado = await mediator.Send(new ConfirmarSubidaDesdeExtensionCommand(id), cancellationToken);
            return resultado.EsFallido
                ? Fallo(resultado.Error)
                : Results.Ok(new { cambioId = resultado.Valor.CambioId, version = resultado.Valor.Version });
        })
        .ConEntradaDeExtension();

        endpoints.MapPost("/extension/acreditaciones/{id:guid}/subida/deshacer", async (
            Guid id, DeshacerSubidaPeticion peticion, IMediator mediator, CancellationToken cancellationToken) =>
        {
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
        var statusCode = error.Codigo is ConfirmarSubidaDesdeExtensionCommandHandler.CodigoConectorInactivo
                or MarcarAcreditacionSubidaCommandHandler.CodigoRechazadaSinVersionNueva
                or DeshacerSubidaDesdeExtensionCommandHandler.CodigoYaNoSePuedeDeshacer
                or ConcurrenciaOptimista.CodigoConflicto
            ? StatusCodes.Status409Conflict
            : StatusCodes.Status404NotFound;
        return Results.Problem(error.Mensaje, statusCode: statusCode);
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