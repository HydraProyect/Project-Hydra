using CaeManager.Infrastructure.Identity;
using System.Security.Claims;
using Microsoft.AspNetCore.StaticAssets;

namespace CaeManager.Web.Services;

/// <summary>
/// Una cuenta a medio activar —contraseña temporal sin cambiar, o Administrador
/// sin 2FA— no alcanza nada fuera de las pantallas que existen para activarla.
///
/// <para>
/// <b>El agujero que cierra.</b> Las dos obligaciones se imponían únicamente en
/// <c>MainLayout</c>, que redirige. Un layout es una pantalla, no un control de
/// acceso: solo corre cuando se renderiza una página. Quien iniciara sesión con
/// una contraseña temporal —enviada por correo, sin caducidad propia— obtenía
/// una cookie de Identity válida, y con ella podía llamar directamente a
/// cualquier endpoint autenticado sin pasar por ningún layout. Entre ellos
/// <c>GET /documentos/{id}/archivo</c>, que no declara autorización propia y
/// por tanto solo exige el <c>FallbackPolicy</c>
/// (<c>RequireAuthenticatedUser</c>): descarga de PDFs con datos de salud sin
/// haber completado la activación de la cuenta. Lo mismo para un Administrador
/// que todavía no hubiera activado la 2FA que su rol exige.
/// </para>
///
/// <para>
/// <b>Qué cierra y qué no.</b> Cierra la vía HTTP, que es por donde se alcanzan
/// los endpoints. La navegación <i>dentro</i> de un circuito de Blazor ya
/// establecido no genera peticiones HTTP nuevas, así que ahí sigue mandando el
/// guard de <c>MainLayout</c>: esto no lo sustituye, lo complementa. Cerrar
/// también esa mitad exige un <c>CircuitHandler</c>, que es un incremento
/// propio.
/// </para>
///
/// <para>
/// <b>Por qué decide con el ticket y no consultando.</b> Poner una consulta a
/// base de datos en el camino de todas las peticiones autenticadas es
/// exactamente lo que el resto de la cadena de resolución evita. El claim lo
/// sella <see cref="TenantClaimsPrincipalFactory"/> y se recalcula en cada
/// inicio de sesión y en el refresco periódico del ticket; los dos flujos que
/// levantan la obligación llaman además a <c>RefreshSignInAsync</c>, así que el
/// desbloqueo es inmediato. Un claim rancio se equivoca hacia seguir exigiendo,
/// nunca hacia dejar pasar.
/// </para>
///
/// <para>
/// <b>Qué se sirve y adónde se redirige.</b> Los estáticos de <c>wwwroot</c> no
/// se bloquean (son los de la pantalla que hay que rellenar y ya se sirven sin
/// sesión), y la navegación se redirige a la pantalla que resuelve lo pendiente:
/// «Cambiar contraseña» si hay contraseña temporal, «Configurar 2FA» si solo
/// falta la 2FA. El destino sale de <c>activacion_pendiente</c>, una pista de
/// pantalla que no autoriza nada; la decisión de bloquear sigue siendo
/// <c>requiere_activacion</c>.
/// </para>
/// </summary>
public class CuentaAMedioActivarSinAccesoMiddleware(RequestDelegate siguiente)
{
    /// <summary>
    /// Todo lo que cuelga de <c>/cuenta/</c>: cambiar la contraseña, configurar
    /// la 2FA, verificarla, cerrar sesión, iniciar sesión. Es el conjunto de
    /// pantallas por las que se sale de este estado, así que bloquearlas
    /// dejaría a la cuenta sin forma de activarse — un cepo, no un control.
    /// </summary>
    private const string PrefijoCuenta = "/cuenta/";

    /// <summary>
    /// Infraestructura del propio Blazor y de la respuesta de error. Cortar
    /// <c>/_blazor</c> tiraría el circuito en vez de redirigir, y el usuario
    /// vería una página rota en lugar de la pantalla que tiene que rellenar.
    /// </summary>
    private static readonly string[] PrefijosDeInfraestructura =
        ["/_blazor", "/_framework", "/_content", "/salud", "/Error", "/not-found"];

    public async Task InvokeAsync(HttpContext contexto)
    {
        if (DebeBloquear(contexto))
        {
            // A una navegación se le contesta con la pantalla que resuelve el
            // problema; a cualquier otra cosa (descarga, exportación, fetch)
            // con un 403 seco, porque redirigir un binario produce un fichero
            // corrupto en vez de un error visible.
            if (EsNavegacion(contexto.Request))
                contexto.Response.Redirect(PantallaDeActivacion(contexto.User));
            else
                contexto.Response.StatusCode = StatusCodes.Status403Forbidden;

            return;
        }

        await siguiente(contexto);
    }

    private static bool DebeBloquear(HttpContext contexto)
    {
        if (contexto.User.Identity?.IsAuthenticated != true) return false;

        if (!contexto.User.HasClaim(TenantClaimsPrincipalFactory.TipoClaimRequiereActivacion, "true"))
            return false;

        // Los CSS/JS/fuentes/imágenes de wwwroot son los de la propia pantalla
        // que la cuenta tiene que rellenar: sin ellos «Cambiar contraseña» sale
        // sin estilos y sin los scripts del formulario. Ya se sirven sin sesión
        // (MapStaticAssets().AllowAnonymous(), Program.cs), así que dejarlos
        // pasar no abre nada que un anónimo no tuviera. Se reconocen por el
        // endpoint que los sirve, no por prefijo de ruta: MapStaticAssets
        // publica también nombres con huella (`tokens.abc123.css`) que ningún
        // prefijo cubre, y una ruta que no sea un asset no lo trae.
        if (contexto.GetEndpoint()?.Metadata.GetMetadata<StaticAssetDescriptor>() is not null)
            return false;

        var ruta = contexto.Request.Path;

        if (ruta.StartsWithSegments(PrefijoCuenta.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return false;

        return !PrefijosDeInfraestructura.Any(
            p => ruta.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// La pantalla que resuelve la obligación pendiente, la misma que elegiría
    /// el guard de <c>MainLayout</c>: contraseña primero, 2FA después. Sin la
    /// pista (cookie emitida antes de existir) se cae en la contraseña, que es
    /// lo que se hacía siempre: un claim rancio se equivoca hacia seguir
    /// exigiendo, nunca hacia dejar pasar.
    /// </summary>
    private static string PantallaDeActivacion(ClaimsPrincipal usuario) =>
        usuario.HasClaim(
            TenantClaimsPrincipalFactory.TipoClaimActivacionPendiente,
            TenantClaimsPrincipalFactory.ActivacionPendienteDosFactores)
        && !usuario.HasClaim(
            TenantClaimsPrincipalFactory.TipoClaimActivacionPendiente,
            TenantClaimsPrincipalFactory.ActivacionPendienteContrasena)
            ? "/cuenta/configurar-2fa"
            : "/cuenta/cambiar-contrasena";

    /// <summary>
    /// Una navegación de nivel superior del navegador, que es lo único que
    /// tiene sentido redirigir. Se decide por <c>Accept</c>, que es lo que
    /// distingue "el usuario ha escrito una URL" de "el código ha pedido un
    /// PDF".
    /// </summary>
    private static bool EsNavegacion(HttpRequest peticion) =>
        HttpMethods.IsGet(peticion.Method)
        && peticion.Headers.Accept.Any(
            a => a is not null && a.Contains("text/html", StringComparison.OrdinalIgnoreCase));
}

public static class CuentaAMedioActivarSinAccesoMiddlewareExtensions
{
    /// <summary>
    /// Registrar después de <c>UseAuthentication</c> —antes no hay principal
    /// que mirar— y antes de que ningún endpoint pueda responder.
    /// </summary>
    public static IApplicationBuilder UseCuentaAMedioActivarSinAcceso(this IApplicationBuilder app) =>
        app.UseMiddleware<CuentaAMedioActivarSinAccesoMiddleware>();
}
