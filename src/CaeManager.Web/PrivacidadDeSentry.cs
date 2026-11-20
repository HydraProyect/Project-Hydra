using System.Text.RegularExpressions;
using Sentry;

namespace CaeManager.Web.Services;

/// <summary>
/// Lo que el SDK de Sentry no saca de la app hacia terceros (OWASP ASVS 4.0.3 V7.1.1 y V8.3.1), aplicado en
/// <c>UseSentry</c> de Program.cs y probado en <c>PrivacidadDeSentryTests</c> (Web.Tests) con este mismo método:
///
/// <list type="bullet">
/// <item>las cabeceras <c>sentry-trace</c> y <c>baggage</c>: el manejador HTTP de Sentry, que la factoría pone en todo
/// <c>HttpClient</c>, las añade por defecto a toda petición saliente (<c>TracePropagationTargets = [".*"]</c>), también a
/// las plataformas CAE externas (dokify, CTAIMACAE.net), a Anthropic o a Microsoft Graph. TALVEG no tiene servicios
/// propios aguas abajo que necesiten la traza distribuida de Sentry, así que la lista queda vacía: no se propaga a
/// ningún host (fallo cerrado; un host que la necesite se añade aquí, con su motivo);</item>
/// <item>el mensaje de las migas de pan: una URL deja solo el esquema y el host (sin credenciales, ruta, consulta ni
/// fragmento) y un correo se tacha. La miga HTTP se queda con el método y ese origen, venga lo que venga en su mensaje,
/// porque puede llevar una ruta relativa con identificadores que no es una URL. Lo que ya quita
/// <see cref="TrazasSinIdentificadoresEnLaRuta.FiltrarMigaDePan"/> (la ruta de una petición marcada) se aplica antes.</item>
/// </list>
///
/// Un GUID suelto fuera de una URL se conserva: la miga de un reintento de análisis IA lleva a propósito el Id del
/// documento (<c>SeguimientoReintentosAnalisisIa</c>).
/// </summary>
public static partial class PrivacidadDeSentry
{
    private const string CorreoTachado = "[correo]";
    private const string UrlTachada = "[url]";

    /// <summary>Para <c>UseSentry</c>: sin propagación de la traza a ningún host y con el filtro de migas de pan.</summary>
    public static void Configurar(SentryOptions opciones)
    {
        opciones.TracePropagationTargets.Clear();
        opciones.SetBeforeBreadcrumb(FiltrarMigaDePan);
    }

    /// <summary>
    /// Para <c>SetBeforeBreadcrumb</c>: aplica <see cref="TrazasSinIdentificadoresEnLaRuta.FiltrarMigaDePan"/> y deja el
    /// mensaje sin datos. Conserva el tipo, los datos, la categoría y el nivel que salen de ese filtro; la hora es la
    /// de la copia (el constructor público de <see cref="Breadcrumb"/> no la recibe), microsegundos después.
    /// </summary>
    public static Breadcrumb? FiltrarMigaDePan(Breadcrumb miga, SentryHint pista)
    {
        if (TrazasSinIdentificadoresEnLaRuta.FiltrarMigaDePan(miga, pista) is not { } filtrada)
            return null;

        var mensaje = EsHttp(filtrada) ? MensajeDeMigaHttp(filtrada) : MensajeSinDatos(filtrada.Message);
        return string.Equals(mensaje, filtrada.Message, StringComparison.Ordinal)
            ? filtrada
            : new Breadcrumb(mensaje ?? string.Empty, filtrada.Type ?? string.Empty, filtrada.Data, filtrada.Category, filtrada.Level);
    }

    private static bool EsHttp(Breadcrumb miga) =>
        string.Equals(miga.Category, "http", StringComparison.Ordinal) || string.Equals(miga.Type, "http", StringComparison.Ordinal);

    private static string MensajeDeMigaHttp(Breadcrumb miga)
    {
        var metodo = miga.Data?.TryGetValue("method", out var valor) == true ? valor : string.Empty;
        var origen = miga.Data?.TryGetValue("url", out var url) == true ? OrigenDe(url) : string.Empty;
        return $"{metodo} {origen}".Trim();
    }

    private static string? MensajeSinDatos(string? mensaje)
    {
        if (string.IsNullOrEmpty(mensaje))
            return mensaje;

        try
        {
            var sinUrls = Url().Replace(mensaje, coincidencia => OrigenDe(coincidencia.Value));
            return Correo().Replace(sinUrls, CorreoTachado);
        }
        catch (RegexMatchTimeoutException)
        {
            // Fallo cerrado: un mensaje que no se pudo revisar a tiempo no sale.
            return string.Empty;
        }
    }

    // Esquema, host y puerto, sin credenciales: lo que identifica al tercero y nada de la petición.
    private static string OrigenDe(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + "/"
            : UrlTachada;

    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9+.\-]*://[^\s""<>]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Url();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Correo();
}
