using System.Globalization;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;

namespace CaeManager.Web.Services;

/// <summary>
/// Único sitio que escribe y borra la cookie de cultura
/// (<see cref="CookieRequestCultureProvider.DefaultCookieName"/>) y que
/// traduce <see cref="IdiomaPreferido"/> a nombre de cultura y viceversa.
///
/// <para>
/// La cookie es una <b>proyección</b> de <c>ApplicationUser.Idioma</c>, nunca
/// la fuente de la preferencia: se reescribe desde la cuenta en los tres
/// inicios de sesión reales (<c>Login.razor</c>, <c>LoginCon2fa.razor</c> y el
/// callback de Microsoft en <c>IdentityEndpointsExtensions</c>), en
/// <c>POST /cuenta/idioma</c> solo después de persistir el cambio, y se borra
/// al cerrar sesión. La lee <see cref="CookieRequestCultureProvider"/>, el
/// único proveedor de <c>RequestLocalizationOptions</c> (Program.cs): ni la
/// query string ni <c>Accept-Language</c> deciden el idioma, porque la
/// preferencia es del usuario, no del navegador.
/// </para>
///
/// <para>
/// <b>Persistente, no de sesión</b>: el inicio de sesión usa
/// <c>isPersistent: true</c>, así que con una cookie de sesión cerrar y
/// reabrir el navegador dejaba la sesión viva en español aunque la cuenta
/// estuviera en catalán. <see cref="Eliminar"/> usa el mismo <c>Path</c> con
/// el que se escribió: un borrado con otro <c>Path</c> no expira la cookie en
/// el navegador.
/// </para>
/// </summary>
public static class CulturaUsuarioCookie
{
    public const string CulturaEspanol = "es-ES";
    public const string CulturaCatalan = "ca-ES";

    /// <summary>Culturas admitidas, con la predeterminada primero (la usan RequestLocalizationOptions y el selector).</summary>
    public static readonly IReadOnlyList<string> CulturasSoportadas = [CulturaEspanol, CulturaCatalan];

    public static string NombreCookie => CookieRequestCultureProvider.DefaultCookieName;

    public static readonly TimeSpan Vigencia = TimeSpan.FromDays(365);

    private const string Ruta = "/";

    public static string ACultura(IdiomaPreferido idioma) => idioma switch
    {
        IdiomaPreferido.Catalan => CulturaCatalan,
        _ => CulturaEspanol,
    };

    /// <summary>
    /// Lista blanca: solo las culturas soportadas, comparadas exactamente. Un
    /// valor desconocido no cae a español en silencio — el llamador decide
    /// qué hacer con un <c>false</c>.
    /// </summary>
    public static bool IntentarDesdeCultura(string? cultura, out IdiomaPreferido idioma)
    {
        switch (cultura)
        {
            case CulturaEspanol:
                idioma = IdiomaPreferido.Espanol;
                return true;
            case CulturaCatalan:
                idioma = IdiomaPreferido.Catalan;
                return true;
            default:
                idioma = IdiomaPreferido.Espanol;
                return false;
        }
    }

    /// <summary>Idioma de la cultura de interfaz en curso (la que resolvió RequestLocalization para esta petición o circuito).</summary>
    public static IdiomaPreferido Actual() =>
        IntentarDesdeCultura(CultureInfo.CurrentUICulture.Name, out var idioma) ? idioma : IdiomaPreferido.Espanol;

    /// <summary>
    /// Las opciones de <c>RequestLocalization</c> de la aplicación (Program.cs
    /// las registra con esto, y los tests ejercitan este mismo método). Único
    /// proveedor: la cookie. Query string y <c>Accept-Language</c> quedan
    /// fuera a propósito; sin cookie, es-ES. El proveedor no puede leer
    /// <c>HttpContext.User</c> —RequestLocalization corre antes de
    /// UseAuthentication— ni debe consultar Identity en cada petición.
    /// </summary>
    public static void ConfigurarLocalizacion(RequestLocalizationOptions opciones)
    {
        var culturas = CulturasSoportadas.ToArray();
        opciones.SetDefaultCulture(CulturaEspanol)
            .AddSupportedCultures(culturas)
            .AddSupportedUICultures(culturas);
        opciones.RequestCultureProviders = [new CookieRequestCultureProvider()];
        // Content-Language en la respuesta: junto con <html lang> (App.razor),
        // la forma objetiva de ver la cultura activa mientras ca-ES tenga el
        // mismo texto que es-ES.
        opciones.ApplyCurrentCultureToResponseHeaders = true;
    }

    public static void Escribir(HttpContext httpContext, IdiomaPreferido idioma)
    {
        var cultura = ACultura(idioma);
        httpContext.Response.Cookies.Append(
            NombreCookie,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(cultura, cultura)),
            Opciones(httpContext, DateTimeOffset.UtcNow.Add(Vigencia)));
    }

    public static void Eliminar(HttpContext httpContext) =>
        httpContext.Response.Cookies.Delete(NombreCookie, Opciones(httpContext, expira: null));

    private static CookieOptions Opciones(HttpContext httpContext, DateTimeOffset? expira) => new()
    {
        Path = Ruta,
        Expires = expira,
        HttpOnly = true,
        // Igual que ClienteActivoSeleccionado: en local sobre HTTP la marca
        // Secure haría que el navegador descartara la cookie sin avisar; tras
        // el proxy, UseForwardedHeaders ya hace que IsHttps refleje el esquema
        // original.
        Secure = httpContext.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        // La cultura no es un dato de seguimiento: sin esto, una política de
        // consentimiento que exija cookies no esenciales la descartaría.
        IsEssential = true,
    };
}
