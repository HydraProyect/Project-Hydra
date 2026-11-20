using CaeManager.Infrastructure.Integraciones;
using OpenTelemetry.Instrumentation.Http;
using Sentry;

namespace CaeManager.Web.Services;

/// <summary>
/// Lo que OpenTelemetry y Sentry no exportan de una petición saliente marcada con
/// <see cref="PeticionConIdentificadoresEnLaRuta"/>: la de un cliente HTTP de plataforma CAE cuya ruta lleva un CIF, un
/// NIF o una matrícula (OWASP ASVS 4.0.3 V7.1.1). El log del <c>HttpClient</c> ya está apagado en ese cliente; esto cubre
/// las otras dos salidas que ven toda petición de la factoría:
///
/// <list type="bullet">
/// <item>la instrumentación HTTP de OpenTelemetry, cuyo span lleva <c>url.full</c> y va a Seq: no se traza;</item>
/// <item>el manejador de Sentry, que deja una miga de pan con la URL y, ante un 5xx, manda un evento con ella a un
/// tercero: la miga se queda con el esquema y el host, y el evento no se envía (el fallo ya queda en el uso de la
/// conexión y en el log del cliente, con el código HTTP).</item>
/// </list>
///
/// Las métricas HTTP no llevan la ruta (solo método, host, puerto, esquema y código), así que no se filtran.
/// </summary>
public static class TrazasSinIdentificadoresEnLaRuta
{
    /// <summary>Opciones de <c>AddHttpClientInstrumentation</c> de las trazas.</summary>
    public static void ConfigurarInstrumentacionHttp(HttpClientTraceInstrumentationOptions opciones) =>
        opciones.FilterHttpRequestMessage = peticion => !PeticionConIdentificadoresEnLaRuta.Lleva(peticion);

    /// <summary>
    /// Para <c>SetBeforeBreadcrumb</c>: la miga HTTP de una petición marcada pierde la ruta y la consulta. La pista de
    /// esa miga no trae la petición (Sentry solo la pone en la del evento), así que se reconoce también por el ámbito
    /// <see cref="PeticionConIdentificadoresEnLaRuta.EnviandoAhora"/> que abre el cliente HTTP alrededor del envío.
    /// </summary>
    public static Breadcrumb? FiltrarMigaDePan(Breadcrumb miga, SentryHint pista)
    {
        var deMarcada = PeticionDe(pista) is not null
            || (PeticionConIdentificadoresEnLaRuta.EnviandoAhora && string.Equals(miga.Category, "http", StringComparison.Ordinal));
        if (!deMarcada)
            return miga;

        var datos = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var clave in (string[])["method", "status_code"])
        {
            if (miga.Data?.TryGetValue(clave, out var valor) == true)
                datos[clave] = valor;
        }

        datos["url"] = miga.Data?.TryGetValue("url", out var url) == true && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority) + "/"
            : string.Empty;
        return new Breadcrumb(miga.Message ?? string.Empty, miga.Type ?? string.Empty, datos, miga.Category, miga.Level);
    }

    /// <summary>Para <c>SetBeforeSend</c>: el evento de una petición marcada no se envía.</summary>
    public static SentryEvent? FiltrarEvento(SentryEvent evento, SentryHint pista) =>
        PeticionDe(pista) is null ? evento : null;

    // La petición viaja en la pista: Sentry pone ahí la respuesta (de la que cuelga la petición) en la miga de una
    // petición HTTP y en el evento de una petición fallida. Se busca por tipo y no por clave.
    private static HttpRequestMessage? PeticionDe(SentryHint pista)
    {
        foreach (var valor in pista.Items.Values)
        {
            var peticion = valor switch
            {
                HttpResponseMessage respuesta => respuesta.RequestMessage,
                HttpRequestMessage solicitud => solicitud,
                _ => null,
            };

            if (PeticionConIdentificadoresEnLaRuta.Lleva(peticion))
                return peticion;
        }

        return null;
    }
}
