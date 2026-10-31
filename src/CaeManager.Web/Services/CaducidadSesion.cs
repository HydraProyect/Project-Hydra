using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CaeManager.Web.Services;

/// <summary>
/// Caducidad de la sesión de la cookie de aplicación (decisiones de 2026-10-28 y
/// 2026-10-31; ASVS 4.0.3 V3.3.2, V3.4.1): 30 minutos sin interacción del usuario,
/// como máximo 12 horas desde el inicio de sesión aunque haya actividad, y cookie
/// no persistente, que termina al cerrar el navegador.
///
/// <para>
/// <b>No persistente.</b> <see cref="Configurar"/> fuerza
/// <see cref="AuthenticationProperties.IsPersistent"/> a <c>false</c> en toda emisión,
/// sea cual sea el camino que la pida. La cookie no lleva <c>Expires</c>; la caducidad
/// real la deciden las marcas de abajo, dentro del ticket.
/// </para>
///
/// <para>
/// <b>Hora de inicio.</b> La primera emisión de la sesión (contraseña, segundo
/// factor, callback de Microsoft) la guarda en
/// <see cref="AuthenticationProperties.Items"/> y como claim del principal, ambos
/// dentro del ticket cifrado. Una reemisión que conserva las propiedades del
/// ticket —el refresco deslizante del manejador de cookies, el del
/// <c>SecurityStampValidator</c> y <c>RefreshSignInAsync</c> al cambiar la
/// contraseña o activar el segundo factor— conserva la hora original: deslizar
/// no reinicia el tope.
/// </para>
///
/// <para>
/// <b>Inactividad.</b> Conexión abierta no es actividad: ni el circuito de SignalR, ni
/// sus pings, ni una reconexión, ni el refresco deslizante cuentan. Solo cuenta la señal
/// <see cref="RutaSenalInteraccion"/>, que <c>wwwroot/js/actividad-sesion.js</c> envía
/// cuando la persona hace clic, escribe o envía un formulario, como mucho una vez por
/// minuto. Mueve <see cref="MarcaInteraccion"/> en el ticket, de modo que todas las
/// pestañas comparten el mismo plazo, y la anota en <see cref="ActividadSesiones"/>
/// para los circuitos abiertos, que no ven la cookie. El dato vive en la cookie del
/// navegador y en memoria: no se persiste en la base, y no es un registro de jornada.
/// El cliente puede mentir con su propia cookie, pero eso no alarga el tope de 12 h;
/// el plazo protege un puesto desatendido, no frente a quien ya tiene la sesión.
/// </para>
///
/// <para>
/// <b>Fallo cerrado.</b> La cookie sin hora de inicio legible o con más de 12 horas se
/// rechaza y se borra, y también la que supera el plazo de inactividad o trae una marca
/// ilegible. La cookie vigente al desplegar este cambio aún no lleva marca de
/// interacción: se le pone en su primera petición, en vez de cerrar sesiones activas,
/// y el tope de 12 h limita esa tolerancia. El circuito de Blazor no hace peticiones
/// HTTP: <see cref="ProveedorAutenticacionRevalidada"/> mira el tope y la inactividad en
/// cada ciclo de revalidación y pasa a anónimo sin consultar la base.
/// </para>
///
/// <para>
/// <b>Identificador de sesión.</b> La cookie sin identificador de sesión
/// (<c>SignInManagerCuentaDesactivada.ClaimSid</c>), es decir, la emitida antes
/// de la revocación en servidor, también se rechaza y se borra aquí, en la primera
/// petición. Que el identificador siga vivo en la base lo comprueba la revalidación
/// del sello (<c>SignInManagerCuentaDesactivada.ValidateSecurityStampAsync</c>),
/// cada 60 s, para la cookie y para el circuito por igual.
/// </para>
///
/// <para>
/// <b>Cookie Secure.</b> Siempre, salvo en Development: el arnés E2E de CI sirve
/// por http://127.0.0.1 en Development, y una cookie Secure no viaja por http.
/// </para>
/// </summary>
public static class CaducidadSesion
{
    /// <summary>Clave en las propiedades del ticket y tipo del claim con la hora de inicio (segundos Unix).</summary>
    public const string MarcaInicio = "talveg:inicio-sesion";

    /// <summary>Clave en las propiedades del ticket y tipo del claim con la última interacción (segundos Unix).</summary>
    public const string MarcaInteraccion = "talveg:ultima-interaccion";

    /// <summary>POST sin cuerpo que envía <c>actividad-sesion.js</c> cuando la persona interactúa.</summary>
    public const string RutaSenalInteraccion = "/cuenta/sesion/actividad";

    public static readonly TimeSpan Inactividad = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DuracionMaxima = TimeSpan.FromHours(12);

    /// <summary>
    /// Vida de la cookie en el manejador: solo es una cota exterior. El plazo efectivo
    /// es <see cref="Inactividad"/>.
    /// </summary>
    public static readonly TimeSpan VidaCookie = TimeSpan.FromHours(8);

    public static void ConfigurarHsts(IServiceCollection servicios) =>
        servicios.AddHsts(opciones => opciones.MaxAge = TimeSpan.FromDays(365));

    public static void Configurar(CookieAuthenticationOptions opciones, IHostEnvironment entorno)
    {
        opciones.ExpireTimeSpan = VidaCookie;
        opciones.SlidingExpiration = true;
        opciones.Cookie.SecurePolicy = entorno.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        opciones.Cookie.HttpOnly = true;
        if (opciones.Cookie.SameSite == SameSiteMode.None)
            opciones.Cookie.SameSite = SameSiteMode.Lax;

        var firmarOriginal = opciones.Events.OnSigningIn;
        opciones.Events.OnSigningIn = async contexto =>
        {
            contexto.Properties.IsPersistent = false;

            // Solo una emisión con propiedades nuevas fija las horas; una que trae las
            // del ticket vigente (aunque ya no valga) las conserva tal cual.
            var ahora = Sello(Reloj(contexto.HttpContext).GetUtcNow());
            if (!contexto.Properties.Items.TryGetValue(MarcaInicio, out var inicio) || inicio is null)
            {
                inicio = ahora;
                contexto.Properties.Items[MarcaInicio] = inicio;
            }
            if (!contexto.Properties.Items.TryGetValue(MarcaInteraccion, out var interaccion) || interaccion is null)
            {
                interaccion = ahora;
                contexto.Properties.Items[MarcaInteraccion] = interaccion;
            }
            PonerMarca(contexto.Principal, MarcaInicio, inicio);
            PonerMarca(contexto.Principal, MarcaInteraccion, interaccion);
            await firmarOriginal(contexto);
        };

        // Envuelve al SecurityStampValidator (y a OmitirRevalidacionDeStampEnRuta):
        // el tope se mira antes de consultar la base, también en /Error.
        var validarOriginal = opciones.Events.OnValidatePrincipal;
        opciones.Events.OnValidatePrincipal = async contexto =>
        {
            var ahora = Reloj(contexto.HttpContext).GetUtcNow();
            var propiedades = contexto.Properties.Items;
            var sinMarcaInteraccion = !propiedades.TryGetValue(MarcaInteraccion, out var interaccion) || interaccion is null;
            if (!propiedades.TryGetValue(MarcaInicio, out var inicio)
                || inicio is null
                || !EsVigente(inicio, ahora)
                // La cookie emitida antes de los identificadores de sesión no se puede revocar:
                // se rechaza en la primera petición, sin esperar a la revalidación del sello.
                || CaeManager.Infrastructure.Identity.SignInManagerCuentaDesactivada.LeerSid(contexto.Principal) is not { } sid
                || (!sinMarcaInteraccion && !SigueActiva(Leer(interaccion), ahora)))
            {
                contexto.RejectPrincipal();
                await contexto.HttpContext.SignOutAsync(contexto.Scheme.Name);
                return;
            }

            // Después de comprobar el plazo: una señal tardía no revive una sesión ya caducada.
            var conInteraccion = sinMarcaInteraccion || EsSenalInteraccion(contexto.Request);
            if (conInteraccion)
                interaccion = Sello(ahora);

            await validarOriginal(contexto);
            if (contexto.Principal is null)
                return;

            if (conInteraccion)
            {
                propiedades[MarcaInteraccion] = interaccion;
                contexto.ShouldRenew = true;
                contexto.HttpContext.RequestServices.GetService<ActividadSesiones>()?.Anotar(sid, ahora);
            }

            // El SecurityStampValidator reemplaza el principal por uno recién
            // construido desde la base, sin las marcas: se le vuelven a poner.
            PonerMarca(contexto.Principal, MarcaInicio, inicio);
            PonerMarca(contexto.Principal, MarcaInteraccion, interaccion!);
        };
    }

    /// <summary>El principal lleva una hora de inicio legible y de hace menos de <see cref="DuracionMaxima"/>.</summary>
    public static bool EsVigente(ClaimsPrincipal principal, DateTimeOffset ahora) =>
        EsVigente(principal.FindFirst(MarcaInicio)?.Value, ahora);

    /// <summary>
    /// ¿Hubo una interacción hace menos de <see cref="Inactividad"/>? Vale la más reciente
    /// entre la del principal y <paramref name="ultimaRegistrada"/>, porque el principal de
    /// un circuito se queda como estaba al conectarse. Sin marca de interacción cuenta el
    /// inicio de sesión, nunca el momento actual.
    /// </summary>
    public static bool SigueActiva(ClaimsPrincipal principal, DateTimeOffset? ultimaRegistrada, DateTimeOffset ahora)
    {
        var marca = Leer(principal.FindFirst(MarcaInteraccion)?.Value) ?? Leer(principal.FindFirst(MarcaInicio)?.Value);
        return SigueActiva(marca is null || marca < ultimaRegistrada ? ultimaRegistrada : marca, ahora);
    }

    private static bool SigueActiva(DateTimeOffset? ultima, DateTimeOffset ahora) =>
        ultima is { } momento && momento <= ahora && ahora - momento < Inactividad;

    private static bool EsSenalInteraccion(HttpRequest peticion) =>
        HttpMethods.IsPost(peticion.Method)
        && peticion.Path.Equals(RutaSenalInteraccion, StringComparison.OrdinalIgnoreCase);

    private static bool EsVigente(string? marca, DateTimeOffset ahora) =>
        Leer(marca) is { } inicio && inicio <= ahora && ahora - inicio < DuracionMaxima;

    private static DateTimeOffset? Leer(string? marca) =>
        long.TryParse(marca, NumberStyles.None, CultureInfo.InvariantCulture, out var segundos)
        && segundos <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(segundos)
            : null;

    private static string Sello(DateTimeOffset momento) =>
        momento.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static TimeProvider Reloj(HttpContext contexto) =>
        contexto.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;

    private static void PonerMarca(ClaimsPrincipal? principal, string tipo, string valor)
    {
        if (principal?.Identity is not ClaimsIdentity identidad) return;
        foreach (var claim in identidad.FindAll(tipo).ToArray()) identidad.RemoveClaim(claim);
        identidad.AddClaim(new Claim(tipo, valor));
    }
}
