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

    /// <summary>
    /// Igual que <see cref="Sanear"/> (el resultado siempre pasa por él, así que nunca es más
    /// permisivo), pero sin la marca de un solo uso <c>desde=login</c>. Es el saneado de los
    /// selectores de MainLayout (idioma, vista de demo, vista de vocabulario): reciben como
    /// <c>returnUrl</c> la URL con la que se creó el circuito, que tras iniciar sesión es
    /// <c>/?desde=login</c>; devolver al usuario ahí reactivaría el aterrizaje D-2 (Mi trabajo)
    /// en vez de dejarle donde estaba. Solo se quita el parámetro exacto
    /// (<see cref="ParametroAterrizaje"/>=<see cref="ValorAterrizajeLogin"/>, comparación
    /// ordinal del parámetro completo, todas sus apariciones); el resto de parámetros y su orden
    /// se conservan. Si la consulta queda vacía se quita también el "?". Un fragmento, si lo hay,
    /// se conserva tras la consulta: se separa antes de tocarla, de modo que un "?" o un
    /// "desde=login" dentro del fragmento no se interpretan como consulta.
    /// </summary>
    public static string SanearParaVolver(string? returnUrl)
    {
        var saneado = Sanear(returnUrl);

        var inicioConsulta = saneado.IndexOf('?');
        if (inicioConsulta < 0)
            return saneado;

        var inicioFragmento = saneado.IndexOf('#');
        if (inicioFragmento >= 0 && inicioFragmento < inicioConsulta)
            return saneado;

        var finConsulta = inicioFragmento >= 0 ? inicioFragmento : saneado.Length;
        var marca = $"{ParametroAterrizaje}={ValorAterrizajeLogin}";
        var parametros = saneado[(inicioConsulta + 1)..finConsulta].Split('&');

        if (!parametros.Any(p => string.Equals(p, marca, StringComparison.Ordinal)))
            return saneado;

        var conservados = parametros
            .Where(p => p.Length > 0 && !string.Equals(p, marca, StringComparison.Ordinal))
            .ToArray();

        var ruta = saneado[..inicioConsulta];
        var consulta = conservados.Length > 0 ? "?" + string.Join('&', conservados) : "";
        var fragmento = inicioFragmento >= 0 ? saneado[inicioFragmento..] : "";
        return ruta + consulta + fragmento;
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
    ///
    /// <para>
    /// Aquí no se comprueba que esa ruta admita GET. Que el <c>returnUrl</c> no sea
    /// la ruta de un endpoint solo-POST (<c>/cuenta/cliente-activo</c>,
    /// <c>/cuenta/cerrar-sesion</c>…) lo garantiza quien lo escribe: el desafío de
    /// la cookie de sesión no guarda el de una petición que no sea GET ni HEAD
    /// (<see cref="CaeManager.Web.Services.OmitirReturnUrlEnPeticionesNoNavegables"/>,
    /// LV-2), y esa petición llega aquí sin <c>returnUrl</c>.
    /// </para>
    /// </summary>
    public static string DestinoTrasLogin(string? returnUrl)
    {
        var saneado = Sanear(returnUrl);
        return saneado == "/" ? $"/?{ParametroAterrizaje}={ValorAterrizajeLogin}" : saneado;
    }
}
