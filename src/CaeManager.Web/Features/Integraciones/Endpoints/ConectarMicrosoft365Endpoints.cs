using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.ConectarBuzonMicrosoft365;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Integraciones;
using CaeManager.Web.Services;
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
            Guid? clienteId, Guid? gestorPropietarioId, string? buzonCompartido, IMicrosoft365GraphClient graphClient,
            IOptions<Microsoft365GraphOptions> opciones, ISolicitudConexionMicrosoft365Repository solicitudRepositorio,
            IUnitOfWork unitOfWork, ICurrentUserService currentUser, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(opciones.Value.UrlPublicaBase))
                return Results.Problem("La integración con Microsoft 365 no tiene configurada la URL pública de la aplicación.", statusCode: StatusCodes.Status503ServiceUnavailable);

            var usuarioId = await currentUser.ObtenerUsuarioActualIdAsync();
            if (usuarioId is null)
                return Results.Unauthorized();

            // Vacío = el buzón propio de quien va a consentir. Con dirección, un compartido: se
            // valida la forma aquí y el acceso real en el callback, con el token ya canjeado.
            string? compartido = null;
            if (!string.IsNullOrWhiteSpace(buzonCompartido))
            {
                // El compartido es el buzón de la organización (decisión R1): ni de una Empresa ni de un
                // Gestor CAE. Se rechaza aquí para no consumir un consentimiento que el Command rechazaría.
                compartido = NormalizarDireccionBuzon(buzonCompartido);
                if (compartido is null || gestorPropietarioId is not null || clienteId is not null)
                    return Results.LocalRedirect("/integraciones?error=buzon-compartido");
            }

            var solicitud = new SolicitudConexionMicrosoft365(usuarioId.Value, clienteId, gestorPropietarioId, DateTime.UtcNow, compartido);
            solicitudRepositorio.Agregar(solicitud);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            var redirectUri = ConstruirRedirectUriCallback(opciones.Value);
            return Results.Redirect(graphClient.ConstruirUrlAutorizacion(redirectUri, solicitud.Id.ToString()));
        }).RequireAuthorization(politica => politica.RequireRole(Roles.Administrador))
        .ExcluidoDelEncargoDeAdministracion();

        endpoints.MapGet("/integraciones/microsoft365-callback", async (
            string? code, string? state, string? error,
            IMicrosoft365GraphClient graphClient, IOptions<Microsoft365GraphOptions> opciones,
            ISolicitudConexionMicrosoft365Repository solicitudRepositorio, IUnitOfWork unitOfWork,
            ICurrentUserService currentUser, IMediator mediator, AltaBuzonMicrosoft365Service altaBuzon,
            ILogger<Program> logger, CancellationToken cancellationToken) =>
        {
            // Solo «access_denied» es la persona cancelando en la pantalla de Microsoft
            // (RFC 6749 § 4.1.2.1): Conexiones lo enseña en tono informativo. Cualquier
            // otro error de OAuth, un callback sin code/state o la URL pública sin
            // configurar son fallos de verdad y no se disfrazan de cancelación.
            if (string.Equals(error, "access_denied", StringComparison.Ordinal))
                return Results.LocalRedirect("/integraciones?error=cancelado");

            if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state) ||
                string.IsNullOrWhiteSpace(opciones.Value.UrlPublicaBase))
            {
                return Results.LocalRedirect("/integraciones?error=autenticacion");
            }

            var usuarioId = await currentUser.ObtenerUsuarioActualIdAsync();
            if (usuarioId is null || !Guid.TryParse(state, out var solicitudId))
                return Results.LocalRedirect("/integraciones?error=solicitud");

            var solicitud = await solicitudRepositorio.ObtenerPorIdAsync(solicitudId, cancellationToken);
            if (solicitud is null || !solicitud.EsValidaPara(usuarioId.Value, DateTime.UtcNow))
            {
                logger.LogWarning("Callback de Microsoft 365 con \"state\" inválido, expirado o de otro usuario — descartado.");
                return Results.LocalRedirect("/integraciones?error=solicitud");
            }

            var clienteId = solicitud.ClienteId;
            var gestorPropietarioId = solicitud.GestorPropietarioId;
            var buzonCompartido = solicitud.BuzonCompartido;

            // Consumo de un solo uso ANTES de canjear el código: aunque el
            // resto del flujo falle a partir de aquí, este "state" nunca
            // vuelve a ser válido para un segundo intento.
            solicitudRepositorio.Eliminar(solicitud);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            var redirectUri = ConstruirRedirectUriCallback(opciones.Value);
            var tokensResultado = await graphClient.IntercambiarCodigoPorTokensAsync(code, redirectUri, cancellationToken);
            if (tokensResultado.EsFallido)
                return Results.LocalRedirect("/integraciones?error=autenticacion");

            // Qué buzón, con qué token y con qué identidad: AltaBuzonMicrosoft365Service, con sus garantías
            // (directorio del inicio de sesión, sonda con el token de aplicación, sin refresh token en el
            // compartido) cubiertas por sus tests.
            var preparado = await altaBuzon.PrepararAsync(tokensResultado.Valor, buzonCompartido, cancellationToken);
            if (preparado.EsFallido)
            {
                return Results.LocalRedirect(preparado.Error.Codigo == AltaBuzonMicrosoft365Service.ErrorSinAccesoCompartido
                    ? "/integraciones?error=sin-acceso-compartido"
                    : "/integraciones?error=autenticacion");
            }

            var buzon = preparado.Valor;
            var comando = new ConectarBuzonMicrosoft365Command(
                buzon.BuzonEmail, buzon.BuzonEmail, clienteId, buzon.AccessToken, buzon.RefreshToken, opciones.Value.UrlPublicaBase,
                buzon.IdentidadBuzon, gestorPropietarioId, DirectorioEntraId: buzon.DirectorioEntraId, ConsentidoPorUsuarioId: usuarioId.Value);

            try
            {
                var resultado = await mediator.Send(comando, cancellationToken);
                return Results.LocalRedirect(RedireccionTrasConectar(resultado));
            }
            catch (FluentValidation.ValidationException)
            {
                // El state ya está consumido: una combinación que el Command rechaza vuelve a Conexiones con
                // su error, no con un 500.
                return Results.LocalRedirect("/integraciones?error=solicitud");
            }
        }).RequireAuthorization(politica => politica.RequireRole(Roles.Administrador))
        .ExcluidoDelEncargoDeAdministracion();

        return endpoints;
    }

    /// <summary>
    /// A dónde vuelve el callback según el resultado del Command. Un buzón ya conectado (aquí o en otra
    /// organización) no es un fallo de las notificaciones. Pública para poder probarla (sin
    /// <c>InternalsVisibleTo</c> en CaeManager.Web).
    /// </summary>
    public static string RedireccionTrasConectar(Result<Guid> resultado) =>
        resultado.EsExitoso
            ? "/integraciones?conectado=true"
            : resultado.Error.Codigo is ConectarBuzonMicrosoft365CommandHandler.CodigoBuzonYaConectado
                or ConectarBuzonMicrosoft365CommandHandler.CodigoBuzonYaConectadoEnEstaOrganizacion
                ? "/integraciones?error=ya-conectado"
                : resultado.Error.Codigo == ConectarBuzonMicrosoft365CommandHandler.CodigoAltaCompartidaEnPausa
                    ? "/integraciones?error=compartidos-en-pausa"
                    : "/integraciones?error=suscripcion";

    /// <summary>
    /// Dirección de buzón bien formada (solo la dirección, sin nombre visible) o null. Pública para poder
    /// probarla (sin <c>InternalsVisibleTo</c> en CaeManager.Web).
    /// </summary>
    public static string? NormalizarDireccionBuzon(string valor)
    {
        var recortado = valor.Trim();
        if (recortado.Length == 0 || recortado.Length > ConexionIntegracion.LongitudMaximaBuzonEmail)
            return null;

        return System.Net.Mail.MailAddress.TryCreate(recortado, out var direccion) &&
            string.Equals(direccion.Address, recortado, StringComparison.OrdinalIgnoreCase)
            ? direccion.Address
            : null;
    }

    private static string ConstruirRedirectUriCallback(Microsoft365GraphOptions opciones) =>
        $"{opciones.UrlPublicaBase!.TrimEnd('/')}/integraciones/microsoft365-callback";
}
