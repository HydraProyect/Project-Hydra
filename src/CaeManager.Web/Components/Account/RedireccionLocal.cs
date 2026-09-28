namespace CaeManager.Web.Components.Account;

/// <summary>
/// Sanea un ReturnUrl recibido por query string antes de redirigir tras el
/// login: solo se aceptan rutas locales. Un valor absoluto ("https://...")
/// o protocolo-relativo ("//atacante.com", "/\atacante.com" — que los
/// navegadores normalizan a URL externa, igual que "/" + tabulador/CR/LF + "/atacante.com",
/// porque descartan esos caracteres) permitiría un open redirect
/// post-login (hallazgo de Project-Hydra-Negocio/MATURITY_REVIEW.md § 5, riesgos medios): la
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

        // Los navegadores eliminan tabulador, CR y LF al interpretar una URL: "/\t/atacante.com"
        // pasa el filtro de la segunda posición y llega como "//atacante.com". Ningún carácter de
        // control tiene sentido en una ruta local, así que se rechazan en cualquier posición.
        foreach (var c in returnUrl)
        {
            if (char.IsControl(c))
                return "/";
        }

        if (returnUrl.Length > 1 && returnUrl[1] is '/' or '\\')
            return "/";

        return returnUrl;
    }
}
