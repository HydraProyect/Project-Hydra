using System.Security.Cryptography;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.ConectarBuzonMicrosoft365;
using CaeManager.Infrastructure.Identity;
using MediatR;
using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Web.Features.Integraciones.Endpoints;

/// <summary>
/// Flujo OAuth delegado de conexión de un buzón de Microsoft 365 (P3-33) —
/// mismo patrón minimal-API de dos saltos que
/// <c>IdentityEndpointsExtensions</c> (challenge/callback), pero un canje de
/// código propio en vez del middleware de OpenIdConnect: aquí hace falta un
/// refresh token de larga duración (<c>offline_access</c>) para leer/enviar
/// correo después, no solo autenticar al usuario de Hydra una vez.
/// </summary>
public static class ConectarMicrosoft365Endpoints
{
    private const string NombreProtector = "CaeManager.Integraciones.OAuthState.v1";

    public static IEndpointRouteBuilder MapConectarMicrosoft365Endpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/integraciones/conectar-microsoft365", (
            Guid? clienteId, HttpContext httpContext, IMicrosoft365GraphClient graphClient, IDataProtectionProvider dataProtectionProvider) =>
        {
            var protector = dataProtectionProvider.CreateProtector(NombreProtector);
            var state = protector.Protect($"{clienteId}|{Guid.NewGuid()}");
            var redirectUri = ConstruirRedirectUriCallback(httpContext);

            return Results.Redirect(graphClient.ConstruirUrlAutorizacion(redirectUri, state));
        }).RequireAuthorization(politica => politica.RequireRole(Roles.Administrador));

        endpoints.MapGet("/integraciones/microsoft365-callback", async (
            string? code, string? state, string? error, HttpContext httpContext,
            IMicrosoft365GraphClient graphClient, IDataProtectionProvider dataProtectionProvider, IMediator mediator,
            ILogger<Program> logger, CancellationToken cancellationToken) =>
        {
            if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
                return Results.LocalRedirect("/integraciones?error=cancelado");

            Guid? clienteId;
            try
            {
                var protector = dataProtectionProvider.CreateProtector(NombreProtector);
                var payload = protector.Unprotect(state);
                var clienteIdTexto = payload.Split('|')[0];
                clienteId = clienteIdTexto == string.Empty ? null : Guid.Parse(clienteIdTexto);
            }
            catch (CryptographicException)
            {
                // "state" no lo generamos nosotros o fue manipulado — mismo
                // criterio de fallo cerrado que el resto de tokens
                // protegidos de la app (ver ClienteActivoSeleccionado).
                logger.LogWarning("Callback de Microsoft 365 con \"state\" inválido — descartado.");
                return Results.LocalRedirect("/integraciones?error=cancelado");
            }

            var redirectUri = ConstruirRedirectUriCallback(httpContext);
            var tokensResultado = await graphClient.IntercambiarCodigoPorTokensAsync(code, redirectUri, cancellationToken);
            if (tokensResultado.EsFallido)
                return Results.LocalRedirect("/integraciones?error=autenticacion");

            var buzonResultado = await graphClient.ObtenerBuzonEmailAsync(tokensResultado.Valor.AccessToken, cancellationToken);
            if (buzonResultado.EsFallido)
                return Results.LocalRedirect("/integraciones?error=autenticacion");

            var notificationUrlBase = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
            var comando = new ConectarBuzonMicrosoft365Command(
                buzonResultado.Valor, buzonResultado.Valor, clienteId,
                tokensResultado.Valor.AccessToken, tokensResultado.Valor.RefreshToken, notificationUrlBase);

            var resultado = await mediator.Send(comando, cancellationToken);
            return resultado.EsExitoso
                ? Results.LocalRedirect("/integraciones?conectado=true")
                : Results.LocalRedirect("/integraciones?error=suscripcion");
        }).RequireAuthorization(politica => politica.RequireRole(Roles.Administrador));

        return endpoints;
    }

    private static string ConstruirRedirectUriCallback(HttpContext httpContext) =>
        $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/integraciones/microsoft365-callback";
}
