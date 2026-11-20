using System.Collections.Frozen;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using CaeManager.Application.Common;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol;

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
/// <item>toda cabecera de la petición o de la respuesta del evento que no esté en <see cref="CabecerasPermitidas"/>
/// (lista blanca, no negra: una cabecera nueva no sale hasta que se añade allí con su motivo), y las cookies de las
/// dos. El <c>Referer</c> que conserva la lista, en la petición o en la respuesta, se queda con su origen.</item>
/// <item>el cuerpo (<c>Data</c>), el entorno (<c>Env</c>) y los demás datos (<c>Other</c>) de la petición del evento,
/// y el cuerpo de su respuesta, vengan de donde vengan: el SDK no lee el cuerpo de la petición entrante ni copia los
/// datos de la <c>Activity</c> (<c>MaxRequestBodySize</c> y <c>IncludeActivityData</c> quedan apagados), y lo que otra
/// vía meta en esos canales tampoco sale.</item>
/// <item>los datos de toda miga de pan que no sea HTTP: sale con su mensaje saneado y sin <c>Data</c>.</item>
/// <item>las transacciones de rendimiento, los perfiles y los logs de Sentry, que no pasan por <c>SetBeforeSend</c>:
/// quedan apagados aunque la configuración los encienda, y una transacción que llegue a cerrarse igualmente (la de
/// una petición entrante con <c>sentry-trace</c> muestreada) se descarta en <c>SetBeforeSendTransaction</c>.</item>
/// </list>
///
/// Un evento o una miga que el fallo cerrado descarta suma uno a la métrica <see cref="NombreMetricaDescartes"/>
/// (exportada a Seq por OTLP, no a Sentry), etiquetada solo con el tipo y el nombre de la excepción del filtro.
///
/// Un GUID suelto fuera de una URL se conserva: la miga de un reintento de análisis IA lleva a propósito el Id del
/// documento (<c>SeguimientoReintentosAnalisisIa</c>).
/// </summary>
public static partial class PrivacidadDeSentry
{
    private const string CorreoTachado = "[correo]";
    private const string UrlTachada = "[url]";

    /// <summary>
    /// Las únicas cabeceras que conserva la petición (o la respuesta) de un evento; las demás se quitan, vengan de la
    /// petición entrante de ASP.NET Core o del 5xx saliente de <c>CaptureFailedRequests</c>. El nombre se compara sin
    /// distinguir mayúsculas de minúsculas (en HTTP/2 llegan en minúsculas). Nunca entran <c>Cookie</c>,
    /// <c>Authorization</c>, <c>X-Forwarded-For</c>, <c>X-Real-IP</c>, ninguna cabecera con un token, una clave o un
    /// secreto, ni las propias de la app (<c>X-Tenant-Beneficiario</c>, <c>X-Ticket-Entrega</c>, <c>X-Talveg-…</c>).
    /// </summary>
    public static readonly FrozenSet<string> CabecerasPermitidas = new[]
    {
        // El cliente HTTP (navegador, robot o integración) y su versión: separa un fallo de un navegador concreto, o de un robot, del general.
        "User-Agent",
        // El tipo de respuesta pedido: separa una petición de página (HTML) de una de API (JSON).
        "Accept",
        // El idioma pedido: un fallo de formato de fecha, número o cultura depende de él.
        "Accept-Language",
        // El tipo del cuerpo enviado (formulario, JSON, fichero); el cuerpo no se envía.
        "Content-Type",
        // La página de procedencia, reducida a su esquema y host en FiltrarEvento: separa un enlace propio de uno externo.
        "Referer",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Contador de los eventos y las migas que el fallo cerrado descarta, en el medidor de
    /// <see cref="Observabilidad.NombreOrigen"/> (Program.cs lo exporta a Seq). Sus únicas etiquetas son <c>tipo</c>
    /// («evento» o «miga») y <c>excepcion</c> (el nombre del tipo de la excepción del filtro): nada del evento ni de la
    /// miga, ni su URL, su mensaje, el usuario o el Tenant.
    /// </summary>
    public const string NombreMetricaDescartes = "caemanager.sentry.descartes_fallo_cerrado";

    private static readonly Meter Medidor = new(Observabilidad.NombreOrigen);

    // Sin «description»: el texto de la métrica lo da el resumen de NombreMetricaDescartes, y una frase aquí contaría como
    // texto sin localizar de Web (TextosSinLocalizarCongeladosTests).
    private static readonly Counter<long> Descartes = Medidor.CreateCounter<long>(NombreMetricaDescartes);

    /// <summary>Para <c>UseSentry</c> sin más filtro de eventos que el de esta clase.</summary>
    public static void Configurar(SentryOptions opciones) => Configurar(opciones, static (evento, _) => evento);

    /// <summary>
    /// Para <c>UseSentry</c>: sin propagación de la traza a ningún host, con el filtro de migas de pan y con el único
    /// <c>SetBeforeSend</c>, que aplica primero <paramref name="filtroDeEventos"/> (el de Program.cs, que necesita la
    /// pista y el destino del evento) y después deja el evento sin la ruta ni la consulta de su petición.
    /// </summary>
    public static void Configurar(SentryOptions opciones, Func<SentryEvent, SentryHint, SentryEvent?> filtroDeEventos)
    {
        // Sin datos personales por defecto, sin transacciones de rendimiento (ni perfiles, que cuelgan de ellas) y sin
        // logs, aunque la configuración (appsettings o Sentry__TracesSampleRate, Sentry__EnableLogs, Sentry__SendDefaultPii)
        // los encienda: UseSentry enlaza la sección «Sentry» antes de este método. Una transacción lleva la petición con
        // sus cabeceras y su URL completa y un log lleva su mensaje, y ninguno de los dos pasa por SetBeforeSend:
        // encenderlos exige darles aquí su filtro con este mismo tratamiento (SetBeforeSendLog, y en SetBeforeSendTransaction
        // uno que sustituya al descarte de abajo).
        opciones.SendDefaultPii = false;
        opciones.TracesSampleRate = null;
        opciones.TracesSampler = null;
        opciones.EnableLogs = false;

        // Sentry.AspNetCore (la de UseSentry): sin leer el cuerpo de la petición entrante ni copiar al evento las etiquetas
        // y el baggage de la Activity, aunque la configuración (Sentry__MaxRequestBodySize, Sentry__IncludeActivityData)
        // los encienda. FiltrarEvento quita además el cuerpo que llegue por otra vía.
        if (opciones is SentryAspNetCoreOptions aspNetCore)
        {
            aspNetCore.MaxRequestBodySize = RequestSize.None;
            aspNetCore.IncludeActivityData = false;
        }

        // Sin trazas muestreadas no basta: una petición entrante con «sentry-trace: <traza>-<span>-1» hereda la decisión
        // de muestreo (sampled=1) y el middleware de trazas de Sentry.AspNetCore (AutoRegisterTracing, activo por
        // defecto) cierra una transacción con la petición entera, que no pasa por SetBeforeSend. Ninguna sale.
        opciones.SetBeforeSendTransaction(static (_, _) => null);
        opciones.TracePropagationTargets.Clear();
        opciones.SetBeforeBreadcrumb((miga, pista) => FiltrarMigaDePanConFalloCerrado(miga, pista, FiltrarMigaDePan));
        opciones.SetBeforeSend((evento, pista) => FiltrarEventoConFalloCerrado(evento, pista, filtroDeEventos));
    }

    /// <summary>
    /// El cuerpo del <c>SetBeforeSend</c> de <see cref="Configurar(SentryOptions, Func{SentryEvent, SentryHint, SentryEvent?})"/>:
    /// aplica <paramref name="filtroDeEventos"/> y después <see cref="FiltrarEvento"/>. Si cualquiera de los dos lanza, el
    /// evento no se envía (fallo cerrado).
    /// </summary>
    public static SentryEvent? FiltrarEventoConFalloCerrado(
        SentryEvent evento, SentryHint pista, Func<SentryEvent, SentryHint, SentryEvent?> filtroDeEventos)
    {
        try
        {
            return filtroDeEventos(evento, pista) is { } filtrado ? FiltrarEvento(filtrado) : null;
        }
        catch (Exception excepcion)
        {
            // Si el callback lanza, el SDK registra el error y envía el evento original, con la ruta y la consulta de su
            // petición (lo fija PrivacidadDeSentryTests). Un evento que no se pudo filtrar no sale: se pierde ese evento
            // en Sentry a cambio de no mandar a un tercero lo que el filtro debía quitar, y queda contado.
            ContarDescarte("evento", excepcion);
            return null;
        }
    }

    /// <summary>
    /// El cuerpo del <c>SetBeforeBreadcrumb</c> de <see cref="Configurar(SentryOptions, Func{SentryEvent, SentryHint, SentryEvent?})"/>,
    /// con <see cref="FiltrarMigaDePan"/> como <paramref name="filtroDeMigas"/>: si el filtro lanza, la miga se descarta
    /// (fallo cerrado).
    /// </summary>
    public static Breadcrumb? FiltrarMigaDePanConFalloCerrado(
        Breadcrumb miga, SentryHint pista, Func<Breadcrumb, SentryHint, Breadcrumb?> filtroDeMigas)
    {
        try
        {
            return filtroDeMigas(miga, pista);
        }
        catch (Exception excepcion)
        {
            // El SDK 6.7.0 ya descarta la miga cuyo callback lanza (lo observa PrivacidadDeSentryTests), pero eso es un
            // detalle suyo: si una versión la guardara, saldría con el siguiente evento con la URL completa o el correo de
            // su mensaje. Una miga que no se pudo filtrar no se guarda, sea cual sea la versión del SDK.
            ContarDescarte("miga", excepcion);
            return null;
        }
    }

    // La señal del descarte: solo el tipo y el nombre de la excepción, nunca su mensaje (puede citar el dato que el filtro
    // no pudo quitar) ni nada del evento o de la miga. Si contar falla, el descarte se mantiene.
    private static void ContarDescarte(string tipo, Exception excepcion)
    {
        try
        {
            Descartes.Add(
                1,
                new KeyValuePair<string, object?>("tipo", tipo),
                new KeyValuePair<string, object?>("excepcion", excepcion.GetType().Name));
        }
        catch (Exception)
        {
            // Sin señal antes que sin fallo cerrado.
        }
    }

    /// <summary>
    /// Para <c>SetBeforeBreadcrumb</c>: aplica <see cref="TrazasSinIdentificadoresEnLaRuta.FiltrarMigaDePan"/> y deja el
    /// mensaje sin datos. La miga HTTP se queda solo con el método, el código de estado y el origen de su URL; la de
    /// otra categoría (navegación, log, consulta) sale sin datos, porque nadie ha revisado qué lleva cada una (la de
    /// navegación, por ejemplo, la ruta de destino). Conserva el tipo, la categoría y el nivel que salen de ese filtro;
    /// la hora es la de la copia (el constructor público de <see cref="Breadcrumb"/> no la recibe), microsegundos después.
    /// </summary>
    public static Breadcrumb? FiltrarMigaDePan(Breadcrumb miga, SentryHint pista)
    {
        if (TrazasSinIdentificadoresEnLaRuta.FiltrarMigaDePan(miga, pista) is not { } filtrada)
            return null;

        if (EsHttp(filtrada))
            return new Breadcrumb(MensajeDeMigaHttp(filtrada), filtrada.Type ?? string.Empty, DatosDeMigaHttp(filtrada), filtrada.Category, filtrada.Level);

        var mensaje = MensajeSinDatos(filtrada.Message);
        return string.Equals(mensaje, filtrada.Message, StringComparison.Ordinal) && filtrada.Data is not { Count: > 0 }
            ? filtrada
            : new Breadcrumb(mensaje ?? string.Empty, filtrada.Type ?? string.Empty, null, filtrada.Category, filtrada.Level);
    }

    /// <summary>
    /// Para el <c>SetBeforeSend</c> de <see cref="Configurar(SentryOptions, Func{SentryEvent, SentryHint, SentryEvent?})"/>:
    /// la petición del evento se queda con el origen de su URL y de su <c>Referer</c>, sin consulta, sin cookies, sin
    /// cuerpo, sin entorno ni otros datos y solo con las <see cref="CabecerasPermitidas"/>; la respuesta, si el evento la
    /// trae, solo con esas cabeceras (su <c>Referer</c>, con el origen), sin cookies y sin cuerpo. El método, el código de
    /// estado y el tamaño del cuerpo de la respuesta no se tocan.
    /// </summary>
    public static SentryEvent FiltrarEvento(SentryEvent evento)
    {
        var peticion = evento.Request;
        if (peticion.Url is { } url)
            peticion.Url = OrigenDe(url);
        peticion.QueryString = null;
        peticion.Cookies = null;
        peticion.Data = null;
        // Env lleva hoy el nombre de la máquina y el puerto del servidor, y Other lo que añada una integración: ninguno
        // está revisado, así que no salen (lista blanca).
        peticion.Env.Clear();
        peticion.Other.Clear();
        DejarSoloCabecerasPermitidas(peticion.Headers);

        // Se mira sin el acceso de SentryContexts.Response, que crearía un contexto vacío en todo evento.
        if (evento.Contexts.TryGetValue(Response.Type, out var contexto) && contexto is Response respuesta)
        {
            respuesta.Cookies = null;
            respuesta.Data = null;
            DejarSoloCabecerasPermitidas(respuesta.Headers);
        }

        return evento;
    }

    // Solo las de la lista blanca, y el Referer reducido a su origen: lo mismo en la petición que en la respuesta.
    private static void DejarSoloCabecerasPermitidas(IDictionary<string, string> cabeceras)
    {
        foreach (var cabecera in cabeceras.Keys.Where(c => !CabecerasPermitidas.Contains(c)).ToList())
            cabeceras.Remove(cabecera);
        foreach (var cabecera in cabeceras.Keys.Where(c => string.Equals(c, "Referer", StringComparison.OrdinalIgnoreCase)).ToList())
            cabeceras[cabecera] = OrigenDe(cabeceras[cabecera]);
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

        return RedactarConFalloCerrado(mensaje, texto =>
            Correo().Replace(Url().Replace(texto, coincidencia => OrigenDe(coincidencia.Value)), CorreoTachado));
    }

    /// <summary>
    /// Aplica <paramref name="redactar"/> y, si una de sus expresiones agota su tiempo, devuelve el mensaje vacío: un
    /// mensaje que no se pudo revisar a tiempo no sale (fallo cerrado).
    /// </summary>
    public static string RedactarConFalloCerrado(string mensaje, Func<string, string> redactar)
    {
        try
        {
            return redactar(mensaje);
        }
        catch (RegexMatchTimeoutException)
        {
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
