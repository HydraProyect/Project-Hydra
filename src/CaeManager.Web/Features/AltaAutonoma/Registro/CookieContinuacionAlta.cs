using CaeManager.Domain.AltaAutonoma;

namespace CaeManager.Web.Features.AltaAutonoma.Registro;

/// <summary>
/// Cookie del credencial de continuación del alta autónoma (A4): lo que permite seguir del correo verificado al paso
/// «Empresa» sin cuenta ni sesión de Identity. El credencial son 256 bits aleatorios; la base guarda solo su SHA-256 y su
/// caducidad (dos horas, nunca más allá de la de la Solicitud de alta), y no viaja nunca en la URL.
/// <list type="bullet">
/// <item><c>HttpOnly</c>: ningún guion de la página lo lee.</item>
/// <item><c>SameSite=Strict</c>: no acompaña a una petición que empiece en otro sitio, así que un formulario ajeno no
/// puede enviar el paso «Empresa» en nombre de quien se registra (además de la antifalsificación del formulario).</item>
/// <item><c>Path=/alta</c>: solo lo recibe el alta, no el resto de la aplicación.</item>
/// <item><c>Secure</c> siempre que la petición llega por HTTPS, como la cookie de Identity (<c>SameAsRequest</c>): en
/// local sobre HTTP el navegador la descartaría sin avisar. Detrás del proxy, <c>UseForwardedHeaders</c> hace que
/// <c>IsHttps</c> refleje el esquema original.</item>
/// </list>
/// No es una sesión: no identifica a ninguna cuenta ni a ningún Tenant, solo a una Solicitud de alta en curso.
/// </summary>
public static class CookieContinuacionAlta
{
    public const string Nombre = "talveg.alta.continuacion";

    public const string Ruta = "/alta";

    public static void Escribir(HttpContext contexto, string credencial)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        ArgumentException.ThrowIfNullOrEmpty(credencial);
        contexto.Response.Cookies.Append(Nombre, credencial, Opciones(contexto, SolicitudAlta.VigenciaCredencialContinuacion));
    }

    public static string? Leer(HttpContext? contexto) => contexto?.Request.Cookies[Nombre];

    public static void Borrar(HttpContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        contexto.Response.Cookies.Delete(Nombre, Opciones(contexto, null));
    }

    private static CookieOptions Opciones(HttpContext contexto, TimeSpan? vigencia) => new()
    {
        HttpOnly = true,
        Path = Ruta,
        Secure = contexto.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        IsEssential = true,
        MaxAge = vigencia,
    };
}
