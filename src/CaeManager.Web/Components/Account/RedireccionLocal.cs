namespace CaeManager.Web.Components.Account;

/// <summary>
/// Sanea un ReturnUrl recibido por query string antes de redirigir tras el
/// login: solo se aceptan rutas locales. Un valor absoluto ("https://...")
/// o protocolo-relativo ("//atacante.com", "/\atacante.com" — que los
/// navegadores normalizan a URL externa) permitiría un open redirect
/// post-login (hallazgo de MATURITY_REVIEW.md § 5, riesgos medios): la
/// víctima inicia sesión en la página legítima y aterriza en un dominio
/// del atacante sin notarlo. Ante cualquier valor sospechoso se redirige
/// a la raíz — perder el "volver a donde estabas" es un coste aceptable,
/// abrir la redirección no.
/// </summary>
public static class RedireccionLocal
{
    public static string Sanear(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
            return "/";

        if (returnUrl[0] != '/')
            return "/";

        if (returnUrl.Length > 1 && returnUrl[1] is '/' or '\\')
            return "/";

        return returnUrl;
    }
}
