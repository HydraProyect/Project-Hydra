namespace CaeManager.Web.Features.Centros.Components;

/// <summary>
/// Convierte la URL de acceso que un Gestor CAE teclea a mano en el canal de un
/// Centro en un destino seguro para un <c>href</c>. El dominio guarda
/// <c>UrlAcceso</c> como texto libre —con o sin esquema («app.twind.io/login»)—
/// y lo que se teclea no es de fiar: un <c>javascript:</c> o un <c>data:</c> en
/// un enlace navegable sería una ejecución de script en el origen de TALVEG.
/// </summary>
public static class UrlPortalCae
{
    /// <summary>
    /// La URL absoluta http(s) a la que apuntar, o <c>null</c> si el texto no
    /// es un portal navegable. Sin esquema se asume https, que es lo que los
    /// portales de plataformas CAE sirven.
    /// </summary>
    public static string? Normalizar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        var candidato = texto.Trim();

        // Un «algo://» que no sea http(s) es otro esquema, no un host sin esquema.
        var conEsquema = candidato.Contains("://", StringComparison.Ordinal)
            ? candidato
            : "https://" + candidato;

        if (!Uri.TryCreate(conEsquema, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;

        return uri.AbsoluteUri;
    }
}
