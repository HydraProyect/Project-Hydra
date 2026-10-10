using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.WebUtilities;

namespace CaeManager.Web.Services;

/// <summary>
/// El desafío de autenticación solo guarda como <c>ReturnUrl</c> la URL de una
/// petición que el navegador pueda repetir navegando (GET o HEAD).
///
/// <para>
/// Por defecto, el handler de cookies redirige a la pantalla de acceso con
/// <c>ReturnUrl</c> = la ruta de la petición desafiada, sea cual sea su método.
/// Tras iniciar sesión el navegador vuelve a esa ruta con un GET
/// (<see cref="CaeManager.Web.Components.Account.RedireccionLocal.DestinoTrasLogin"/>).
/// Si la petición desafiada era el POST de un formulario contra un endpoint que
/// solo admite POST —cambiar de Tenant activo, cerrar sesión, cambiar de idioma,
/// abrir o cerrar una Sesión Privilegiada—, ese GET no tiene quien lo atienda y
/// el usuario aterriza en un 405 en vez de en la aplicación (LV-2). Basta con
/// que la sesión caduque, o se cierre en otra pestaña, con la página todavía
/// abierta.
/// </para>
///
/// <para>
/// Se corrige donde nace el <c>ReturnUrl</c> y no con una lista de rutas en
/// quien lo consume: la lista habría que mantenerla al día con cada endpoint
/// solo-POST nuevo, y el método de la petición ya dice todo lo necesario. Una
/// petición que no es GET ni HEAD se redirige a la pantalla de acceso sin
/// <c>ReturnUrl</c>, y el inicio de sesión lleva entonces al destino por
/// defecto. El coste es el mismo que acepta <c>RedireccionLocal</c>: perder el
/// «volver a donde estabas» —aquí, también el de un formulario enviado a una
/// página que sí admite GET— antes que aterrizar en un error.
/// </para>
///
/// <para>
/// Solo se toca el <c>ReturnUrl</c> que el handler dedujo de la propia
/// petición. Un desafío con <c>AuthenticationProperties.RedirectUri</c>
/// explícito lo decidió código nuestro y se respeta. Tampoco cambia cómo se
/// responde (302, o 401 para una petición AJAX): eso sigue siendo del evento
/// original, que se conserva y se invoca siempre.
/// </para>
///
/// <para>
/// Límite: no protege de un <c>ReturnUrl</c> escrito a mano en la URL de la
/// pantalla de acceso. Ese caso equivale a pedir una dirección que no existe y
/// no lo produce ningún flujo de la aplicación.
/// </para>
/// </summary>
public static class OmitirReturnUrlEnPeticionesNoNavegables
{
    public static void Configurar(CookieAuthenticationOptions options)
    {
        var redirigirOriginal = options.Events.OnRedirectToLogin;
        options.Events.OnRedirectToLogin = contexto =>
        {
            var deducidoDeLaPeticion = string.IsNullOrEmpty(contexto.Properties.RedirectUri);
            if (deducidoDeLaPeticion && !EsNavegable(contexto.Request.Method))
                contexto.RedirectUri = SinParametro(contexto.RedirectUri, contexto.Options.ReturnUrlParameter);

            return redirigirOriginal(contexto);
        };
    }

    private static bool EsNavegable(string metodo) => HttpMethods.IsGet(metodo) || HttpMethods.IsHead(metodo);

    private static string SinParametro(string uri, string parametro)
    {
        var inicioConsulta = uri.IndexOf('?');
        if (inicioConsulta < 0)
            return uri;

        var conservados = QueryHelpers.ParseQuery(uri[(inicioConsulta + 1)..])
            .Where(par => !string.Equals(par.Key, parametro, StringComparison.OrdinalIgnoreCase))
            .SelectMany(par => par.Value, (par, valor) => KeyValuePair.Create(par.Key, valor));

        return QueryHelpers.AddQueryString(uri[..inicioConsulta], conservados);
    }
}
