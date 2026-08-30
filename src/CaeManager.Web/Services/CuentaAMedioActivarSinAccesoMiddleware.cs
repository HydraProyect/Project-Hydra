using CaeManager.Infrastructure.Identity;

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
                contexto.Response.Redirect("/cuenta/cambiar-contrasena");
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

        var ruta = contexto.Request.Path;

        if (ruta.StartsWithSegments(PrefijoCuenta.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return false;

        return !PrefijosDeInfraestructura.Any(
            p => ruta.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));
    }

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
