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
/// <item>la URL de toda petición HTTP, marcada o no: el dato <c>url</c> de la miga HTTP y la <c>Request.Url</c> del
/// evento (el 5xx saliente de <c>CaptureFailedRequests</c> o la petición entrante de ASP.NET Core) se quedan con el
/// origen, la consulta va vacía y la cabecera <c>Referer</c> se queda también con el origen. En Microsoft Graph la ruta
/// lleva el correo del buzón (<c>/users/{correo}/…</c>) y la consulta, filtros. El método y el código de estado se
/// conservan para el diagnóstico.</item>
/// </list>
///
/// Un GUID suelto fuera de una URL se conserva: la miga de un reintento de análisis IA lleva a propósito el Id del
/// documento (<c>SeguimientoReintentosAnalisisIa</c>).
/// </summary>
public static partial class PrivacidadDeSentry
{
    private const string CorreoTachado = "[correo]";
    private const string UrlTachada = "[url]";

    /// <summary>Para <c>UseSentry</c> sin más filtro de eventos que el de esta clase.</summary>
    public static void Configurar(SentryOptions opciones) => Configurar(opciones, static (evento, _) => evento);

    /// <summary>
    /// Para <c>UseSentry</c>: sin propagación de la traza a ningún host, con el filtro de migas de pan y con el único
    /// <c>SetBeforeSend</c>, que aplica primero <paramref name="filtroDeEventos"/> (el de Program.cs, que necesita la
    /// pista y el destino del evento) y después deja el evento sin la ruta ni la consulta de su petición.
    /// </summary>
    public static void Configurar(SentryOptions opciones, Func<SentryEvent, SentryHint, SentryEvent?> filtroDeEventos)
    {
        opciones.TracePropagationTargets.Clear();
        opciones.SetBeforeBreadcrumb(FiltrarMigaDePan);
        opciones.SetBeforeSend((evento, pista) => filtroDeEventos(evento, pista) is { } filtrado ? FiltrarEvento(filtrado) : null);
    }

    /// <summary>
    /// Para <c>SetBeforeBreadcrumb</c>: aplica <see cref="TrazasSinIdentificadoresEnLaRuta.FiltrarMigaDePan"/> y deja el
    /// mensaje sin datos. La miga HTTP se queda solo con el método, el código de estado y el origen de su URL; la de
    /// otra categoría conserva sus datos. Conserva el tipo, la categoría y el nivel que salen de ese filtro; la hora es
    /// la de la copia (el constructor público de <see cref="Breadcrumb"/> no la recibe), microsegundos después.
    /// </summary>
    public static Breadcrumb? FiltrarMigaDePan(Breadcrumb miga, SentryHint pista)
    {
        if (TrazasSinIdentificadoresEnLaRuta.FiltrarMigaDePan(miga, pista) is not { } filtrada)
            return null;

        if (EsHttp(filtrada))
            return new Breadcrumb(MensajeDeMigaHttp(filtrada), filtrada.Type ?? string.Empty, DatosDeMigaHttp(filtrada), filtrada.Category, filtrada.Level);

        var mensaje = MensajeSinDatos(filtrada.Message);
        return string.Equals(mensaje, filtrada.Message, StringComparison.Ordinal)
            ? filtrada
            : new Breadcrumb(mensaje ?? string.Empty, filtrada.Type ?? string.Empty, filtrada.Data, filtrada.Category, filtrada.Level);
    }

    /// <summary>
    /// Para el <c>SetBeforeSend</c> de <see cref="Configurar(SentryOptions, Func{SentryEvent, SentryHint, SentryEvent?})"/>:
    /// la petición del evento se queda con el origen de su URL y de su <c>Referer</c>, y sin consulta. El método y el
    /// contexto <c>response</c> (código de estado) no se tocan.
    /// </summary>
    public static SentryEvent FiltrarEvento(SentryEvent evento)
    {
        var peticion = evento.Request;
        if (peticion.Url is { } url)
            peticion.Url = OrigenDe(url);
        peticion.QueryString = null;
        foreach (var cabecera in peticion.Headers.Keys.Where(c => string.Equals(c, "Referer", StringComparison.OrdinalIgnoreCase)).ToList())
            peticion.Headers[cabecera] = OrigenDe(peticion.Headers[cabecera]);
        return evento;
    }

    private static Dictionary<string, string> DatosDeMigaHttp(Breadcrumb miga)
    {
        var datos = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var clave in (string[])["method", "status_code"])
        {
            if (miga.Data?.TryGetValue(clave, out var valor) == true)
                datos[clave] = valor;
        }

        if (miga.Data?.TryGetValue("url", out var url) == true)
            datos["url"] = OrigenDe(url);
        return datos;
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
