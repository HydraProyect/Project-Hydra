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
            // Sin la URL pública no hay redirect_uri: no se arranca el flujo. Conexiones y Buzones ya no ofrecen el enlace en
            // ese caso; quien llega igualmente (un favorito, un enlace viejo) vuelve a Conexiones con su aviso, no a un JSON de
            // problema en el navegador. Es la misma comprobación de antes, solo cambia la respuesta (RequireAuthorization sigue delante).
            if (string.IsNullOrWhiteSpace(opciones.Value.UrlPublicaBase))
                return Results.LocalRedirect(RedireccionSinUrlPublica);
            // Con URL pública pero sin ClientId o sin credencial, Microsoft recibiría un client_id vacío y enseñaría su propia
            // página de error; el callback tampoco podría canjear el código. Se dice aquí, con el mismo código que el callback.
            if (!opciones.Value.EstaConfigurado)
                return Results.LocalRedirect(RedireccionConfiguracionIncompleta);

            var usuarioId = await currentUser.ObtenerUsuarioActualIdAsync();
            if (usuarioId is null)
                return Results.Unauthorized();

            // RequireRole mira el rol del claim de sesión; el Command exige el efectivo, que dentro de un Workspace
            // operativo derivado puede ser otro. Se comprueba aquí también para no pedir un consentimiento en
            // Microsoft que el Command rechazaría: la frontera sigue siendo el handler.
            if (await currentUser.ObtenerRolEfectivoAsync() != Roles.Administrador)
                return Results.LocalRedirect(RedireccionSoloAdministrador);

            // Vacío = el buzón propio de quien va a consentir. Con dirección, un compartido: se
            // valida la forma aquí y el acceso real en el callback, con el token ya canjeado.
            string? compartido = null;
            if (!string.IsNullOrWhiteSpace(buzonCompartido))
            {
                // El compartido es el buzón de la organización (decisión R1): ni de una Empresa ni de un
                // Gestor CAE. Se rechaza aquí para no consumir un consentimiento que el Command rechazaría.
                compartido = NormalizarDireccionBuzon(buzonCompartido);
                if (compartido is null || gestorPropietarioId is not null || clienteId is not null)
                    return Results.LocalRedirect(RedireccionBuzonCompartido);
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
            // otro error de OAuth es un fallo de verdad y no se disfraza de cancelación. El texto del error que devuelve
            // Microsoft nunca viaja de vuelta: solo el código cerrado (Conexiones muestra un mensaje fijo por código).
            if (string.Equals(error, "access_denied", StringComparison.Ordinal))
                return Results.LocalRedirect(RedireccionCancelado);

            if (!string.IsNullOrWhiteSpace(error))
                return Results.LocalRedirect(RedireccionErrorMicrosoft);

            // Falta configuración de verdad (URL pública, ClientId o credencial): solo aquí sale el aviso de configuración.
            if (string.IsNullOrWhiteSpace(opciones.Value.UrlPublicaBase))
                return Results.LocalRedirect(RedireccionSinUrlPublica);
            if (!opciones.Value.EstaConfigurado)
                return Results.LocalRedirect(RedireccionConfiguracionIncompleta);

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
                return Results.LocalRedirect(RedireccionSolicitud);

            var usuarioId = await currentUser.ObtenerUsuarioActualIdAsync();
            if (usuarioId is null || !Guid.TryParse(state, out var solicitudId))
                return Results.LocalRedirect(RedireccionSolicitud);

            var solicitud = await solicitudRepositorio.ObtenerPorIdAsync(solicitudId, cancellationToken);
            if (solicitud is null || !solicitud.EsValidaPara(usuarioId.Value, DateTime.UtcNow))
            {
                logger.LogWarning("Callback de Microsoft 365 con \"state\" inválido, expirado o de otro usuario — descartado.");
                return Results.LocalRedirect(RedireccionSolicitud);
            }

            var clienteId = solicitud.ClienteId;
            var gestorPropietarioId = solicitud.GestorPropietarioId;
            var buzonCompartido = solicitud.BuzonCompartido;

            // Consumo de un solo uso ANTES de canjear el código: aunque el
            // resto del flujo falle a partir de aquí, este "state" nunca
            // vuelve a ser válido para un segundo intento.
            try
            {
                solicitudRepositorio.Eliminar(solicitud);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Otro callback con el mismo state lo consumió antes (concurrencia) o la base no respondió: no se canjea
                // nada y se vuelve con «solicitud», nunca con un 500.
                logger.LogWarning(ex, "No se pudo consumir el \"state\" del callback de Microsoft 365 — descartado.");
                return Results.LocalRedirect(RedireccionSolicitud);
            }

            try
            {
                var redirectUri = ConstruirRedirectUriCallback(opciones.Value);
                var tokensResultado = await graphClient.IntercambiarCodigoPorTokensAsync(code, redirectUri, cancellationToken);
                if (tokensResultado.EsFallido)
                    return Results.LocalRedirect(RedireccionCanjeTokens);

                // Qué buzón, con qué token y con qué identidad: AltaBuzonMicrosoft365Service, con sus garantías
                // (directorio del inicio de sesión, sonda con el token de aplicación, sin refresh token en el
                // compartido) cubiertas por sus tests.
                var preparado = await altaBuzon.PrepararAsync(tokensResultado.Valor, buzonCompartido, cancellationToken);
                if (preparado.EsFallido)
                    return Results.LocalRedirect(RedireccionTrasFalloDeAlta(preparado.Error.Codigo));

                var buzon = preparado.Valor;
                var comando = new ConectarBuzonMicrosoft365Command(
                    buzon.BuzonEmail, buzon.BuzonEmail, clienteId, buzon.AccessToken, buzon.RefreshToken, opciones.Value.UrlPublicaBase,
                    buzon.IdentidadBuzon, gestorPropietarioId, DirectorioEntraId: buzon.DirectorioEntraId, ConsentidoPorUsuarioId: usuarioId.Value);

                var resultado = await mediator.Send(comando, cancellationToken);
                return Results.LocalRedirect(RedireccionTrasConectar(resultado));
            }
            catch (FluentValidation.ValidationException)
            {
                // El state ya está consumido: una combinación que el Command rechaza vuelve a Conexiones con
                // su error, no con un 500.
                return Results.LocalRedirect(RedireccionSolicitud);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Lo que el cliente Graph no traduce a Result (límite de tiempo o circuito abierto de la resiliencia, un
                // cuerpo que no es JSON) vuelve con el aviso genérico de autenticación, no con un 500 ni con /Error.
                logger.LogError(ex, "Fallo inesperado al completar la conexión de Microsoft 365 tras consumir el \"state\".");
                return Results.LocalRedirect(RedireccionAutenticacion);
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
                ? RedireccionYaConectado
                : resultado.Error.Codigo == ConectarBuzonMicrosoft365CommandHandler.CodigoAltaCompartidaEnPausa
                    ? RedireccionCompartidosEnPausa
                    : resultado.Error.Codigo == ConectarBuzonMicrosoft365CommandHandler.CodigoSoloAdministrador
                        ? RedireccionSoloAdministrador
                        : RedireccionSuscripcion;

    /// <summary>
    /// A dónde vuelve el callback cuando falla la preparación del alta (<see cref="AltaBuzonMicrosoft365Service"/>). Se clasifica
    /// por el código del fallo, que sale del estado HTTP o del código de error de Graph, nunca del texto que devuelva Microsoft;
    /// cualquier otro cae en «autenticacion», genérico. Pública para poder probarla.
    /// </summary>
    public static string RedireccionTrasFalloDeAlta(string codigoFallo) => codigoFallo switch
    {
        AltaBuzonMicrosoft365Service.ErrorSinAccesoCompartido => RedireccionSinAccesoCompartido,
        AltaBuzonMicrosoft365Service.ErrorSinPermisos => RedireccionPermisosMicrosoft,
        AltaBuzonMicrosoft365Service.ErrorSinBuzonExchange => RedireccionSinBuzonExchange,
        _ => RedireccionAutenticacion
    };

    // Códigos cerrados de «?error=»: Conexiones pinta un texto fijo por código y uno desconocido cae en el genérico.
    // Son rutas fijas de la propia aplicación (LocalRedirect): nada de lo que llega en la query ni de Microsoft se refleja.
    public const string RedireccionCancelado = "/integraciones?error=cancelado";
    public const string RedireccionErrorMicrosoft = "/integraciones?error=error-microsoft";
    public const string RedireccionConfiguracionIncompleta = "/integraciones?error=configuracion";
    public const string RedireccionSolicitud = "/integraciones?error=solicitud";
    public const string RedireccionCanjeTokens = "/integraciones?error=canje-tokens";
    public const string RedireccionPermisosMicrosoft = "/integraciones?error=permisos-microsoft";
    public const string RedireccionSinBuzonExchange = "/integraciones?error=sin-buzon-exchange";
    public const string RedireccionSinAccesoCompartido = "/integraciones?error=sin-acceso-compartido";
    public const string RedireccionAutenticacion = "/integraciones?error=autenticacion";
    public const string RedireccionBuzonCompartido = "/integraciones?error=buzon-compartido";
    public const string RedireccionYaConectado = "/integraciones?error=ya-conectado";
    public const string RedireccionCompartidosEnPausa = "/integraciones?error=compartidos-en-pausa";
    public const string RedireccionSuscripcion = "/integraciones?error=suscripcion";

    /// <summary>
    /// Sin rol efectivo Administrador no se conecta nada; no es un fallo de las notificaciones, así que no vuelve con
    /// «suscripcion» (que invita a repetir). Conexiones lo muestra con su texto propio.
    /// </summary>
    public const string RedireccionSoloAdministrador = "/integraciones?error=solo-administrador";

    /// <summary>
    /// Falta la URL pública de la aplicación (configuración del entorno, no de la persona): Conexiones lo explica y manda a
    /// Soporte TALVEG. Pública para poder probarla (sin <c>InternalsVisibleTo</c> en CaeManager.Web).
    /// </summary>
    public const string RedireccionSinUrlPublica = "/integraciones?error=url-publica";

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
