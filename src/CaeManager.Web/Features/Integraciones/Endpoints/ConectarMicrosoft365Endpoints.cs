using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.ConectarBuzonMicrosoft365;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Integraciones;
using MediatR;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Features.Integraciones.Endpoints;

/// <summary>
/// Flujo OAuth delegado de conexión de un buzón de Microsoft 365 (P3-33) —
/// mismo patrón minimal-API de dos saltos que
/// <c>IdentityEndpointsExtensions</c> (challenge/callback), pero un canje de
/// código propio en vez del middleware de OpenIdConnect: aquí hace falta un
/// refresh token de larga duración (<c>offline_access</c>) para leer/enviar
/// correo después, no solo autenticar al usuario de Hydra una vez.
///
/// El "state" es un nonce de un solo uso persistido en
/// <see cref="SolicitudConexionMicrosoft365"/> (auditoría módulo 6) — nunca
/// un payload cifrado sin ligar a nadie: eso permitía un OAuth
/// account-linking CSRF (un atacante autoriza su propio buzón y hace que la
/// víctima complete el callback, conectando el buzón del atacante dentro
/// del tenant de la víctima). RLS ya impide leer una fila de otro tenant;
/// <see cref="SolicitudConexionMicrosoft365.EsValidaPara"/> exige además que
/// quien completa el callback sea quien inició el flujo, y la fila se borra
/// al consumirse.
/// </summary>
public static class ConectarMicrosoft365Endpoints
{
    public static IEndpointRouteBuilder MapConectarMicrosoft365Endpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/integraciones/conectar-microsoft365", async (
            Guid? clienteId, Guid? gestorPropietarioId, IMicrosoft365GraphClient graphClient,
            IOptions<Microsoft365GraphOptions> opciones, ISolicitudConexionMicrosoft365Repository solicitudRepositorio,
            IUnitOfWork unitOfWork, ICurrentUserService currentUser, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(opciones.Value.UrlPublicaBase))
                return Results.Problem("La integración con Microsoft 365 no tiene configurada la URL pública de la aplicación.", statusCode: StatusCodes.Status503ServiceUnavailable);

            var usuarioId = await currentUser.ObtenerUsuarioActualIdAsync();
            if (usuarioId is null)
                return Results.Unauthorized();

            var solicitud = new SolicitudConexionMicrosoft365(usuarioId.Value, clienteId, gestorPropietarioId, DateTime.UtcNow);
            solicitudRepositorio.Agregar(solicitud);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            var redirectUri = ConstruirRedirectUriCallback(opciones.Value);
            return Results.Redirect(graphClient.ConstruirUrlAutorizacion(redirectUri, solicitud.Id.ToString()));
        }).RequireAuthorization(politica => politica.RequireRole(Roles.Administrador));

        endpoints.MapGet("/integraciones/microsoft365-callback", async (
            string? code, string? state, string? error,
            IMicrosoft365GraphClient graphClient, IOptions<Microsoft365GraphOptions> opciones,
            ISolicitudConexionMicrosoft365Repository solicitudRepositorio, IUnitOfWork unitOfWork,
            ICurrentUserService currentUser, IMediator mediator, ILogger<Program> logger, CancellationToken cancellationToken) =>
        {
            if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state) ||
                string.IsNullOrWhiteSpace(opciones.Value.UrlPublicaBase))
            {
                return Results.LocalRedirect("/integraciones?error=cancelado");
            }

            var usuarioId = await currentUser.ObtenerUsuarioActualIdAsync();
            if (usuarioId is null || !Guid.TryParse(state, out var solicitudId))
                return Results.LocalRedirect("/integraciones?error=cancelado");

            var solicitud = await solicitudRepositorio.ObtenerPorIdAsync(solicitudId, cancellationToken);
            if (solicitud is null || !solicitud.EsValidaPara(usuarioId.Value, DateTime.UtcNow))
            {
                logger.LogWarning("Callback de Microsoft 365 con \"state\" inválido, expirado o de otro usuario — descartado.");
                return Results.LocalRedirect("/integraciones?error=cancelado");
            }

            var clienteId = solicitud.ClienteId;
            var gestorPropietarioId = solicitud.GestorPropietarioId;

            // Consumo de un solo uso ANTES de canjear el código: aunque el
            // resto del flujo falle a partir de aquí, este "state" nunca
            // vuelve a ser válido para un segundo intento.
            solicitudRepositorio.Eliminar(solicitud);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            var redirectUri = ConstruirRedirectUriCallback(opciones.Value);
            var tokensResultado = await graphClient.IntercambiarCodigoPorTokensAsync(code, redirectUri, cancellationToken);
            if (tokensResultado.EsFallido)
                return Results.LocalRedirect("/integraciones?error=autenticacion");

            var buzonResultado = await graphClient.ObtenerBuzonEmailAsync(tokensResultado.Valor.AccessToken, cancellationToken);
            if (buzonResultado.EsFallido)
                return Results.LocalRedirect("/integraciones?error=autenticacion");

            var comando = new ConectarBuzonMicrosoft365Command(
                buzonResultado.Valor, buzonResultado.Valor, clienteId,
                tokensResultado.Valor.AccessToken, tokensResultado.Valor.RefreshToken, opciones.Value.UrlPublicaBase, gestorPropietarioId);

            var resultado = await mediator.Send(comando, cancellationToken);
            return resultado.EsExitoso
                ? Results.LocalRedirect("/integraciones?conectado=true")
                : Results.LocalRedirect("/integraciones?error=suscripcion");
        }).RequireAuthorization(politica => politica.RequireRole(Roles.Administrador));

        return endpoints;
    }

    private static string ConstruirRedirectUriCallback(Microsoft365GraphOptions opciones) =>
        $"{opciones.UrlPublicaBase!.TrimEnd('/')}/integraciones/microsoft365-callback";
}
