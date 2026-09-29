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

    /// <summary>Parámetro de un solo uso que Inicio consume (aterrizaje D-2).</summary>
    public const string ParametroAterrizaje = "desde";

    /// <summary>Valor del parámetro que añade el flujo de inicio de sesión.</summary>
    public const string ValorAterrizajeLogin = "login";

    /// <summary>
    /// Destino tras autenticar (login, 2FA, inicio con Microsoft). Sin
    /// <c>returnUrl</c> explícito a otra ruta, el destino es Inicio marcado con
    /// <c>?desde=login</c>: la marca de un solo uso con la que Inicio sabe que
    /// es el aterrizaje tras iniciar sesión y no una visita por el menú (D-2,
    /// 2026-09-22: Mi trabajo es el aterrizaje del Gestor CAE de un Operador
    /// CAE externo, no lo que responde cada clic en «Inicio»). Con
    /// <c>returnUrl</c> explícito a otra ruta, se respeta tal cual.
    /// </summary>
    public static string DestinoTrasLogin(string? returnUrl)
    {
        var saneado = Sanear(returnUrl);
        return saneado == "/" ? $"/?{ParametroAterrizaje}={ValorAterrizajeLogin}" : saneado;
    }
}
