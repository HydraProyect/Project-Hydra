using System.Collections.Concurrent;
using System.Net;
using CaeManager.Application.Integraciones.ConectoresPlataformaCae;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.Dokify;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sentry;
using Sentry.AspNetCore;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// <see cref="PrivacidadDeSentry.Configurar"/>, el mismo método que aplica <c>UseSentry</c> en Program.cs: sin
/// <c>sentry-trace</c> ni <c>baggage</c> hacia ningún host, con el <see cref="SentryHttpMessageHandler"/> real y el SDK
/// iniciado (control: sin la configuración, el SDK sí las manda), y migas de pan sin URLs, consultas ni correos en su
/// mensaje.
/// </summary>
[Collection(ColeccionInstrumentacionGlobal.Nombre)]
public class PrivacidadDeSentryTests
{
    private const string Guid = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string Correo = "ana.prueba@ejemplo.test";

    private static readonly string[] Destinos =
    [
        "https://api.dokify.net/v1/employee/TEST0002B/docs",
        "https://cliente.ctaima.com/api/v2/workers",
        "https://host-arbitrario.invalid/ruta",
    ];

    [Fact]
    public void Tras_la_configuracion_la_lista_de_propagacion_queda_vacia_y_por_defecto_no_lo_esta()
    {
        new SentryOptions().TracePropagationTargets.Should().NotBeEmpty("control: el valor por defecto propaga a todo host");

        var opciones = new SentryOptions();
        PrivacidadDeSentry.Configurar(opciones);

        opciones.TracePropagationTargets.Should().BeEmpty();
    }

    [Fact]
    public async Task Tras_la_configuracion_ninguna_peticion_saliente_lleva_sentry_trace_ni_baggage_y_sin_ella_si()
    {
        var sinConfigurar = await CabecerasEnviadasAsync(_ => { });
        var configurado = await CabecerasEnviadasAsync(PrivacidadDeSentry.Configurar);

        foreach (var destino in Destinos)
        {
            sinConfigurar[destino].Should().Contain("sentry-trace", $"control: el SDK por defecto propaga a {destino}");
            configurado[destino].Should().NotContain("sentry-trace", destino).And.NotContain("baggage", destino);
        }
    }

    [Fact]
    public async Task Con_un_host_real_el_enlace_de_configuracion_no_vuelve_a_llenar_la_lista_de_propagacion()
    {
        // La configuración trae Sentry:TracePropagationTargets:0 = "control.invalid" (como la traería una variable de entorno
        // o un appsettings), un valor distinto del por defecto del SDK (".*"): el enlace de la sección "Sentry" corre sobre
        // SentryAspNetCoreOptions y no debe pisar a Configurar.
        var enlazada = await ListaDePropagacionDelHostAsync(PrivacidadDeSentry.Configurar);
        var control = await ListaDePropagacionDelHostAsync(_ => { });

        control.Should().Equal(["control.invalid"], "control: sin Configurar, el enlace sustituye el valor por defecto; si no corriera, la lista sería la de por defecto");
        enlazada.Should().BeEmpty();
        // Que el SDK global quede cerrado para el resto de la colección lo garantiza el finally de ListaDePropagacionDelHostAsync.
    }

    // El tiempo agotado se provoca lanzando la excepción y no con una entrada enorme: el generador de expresiones busca
    // el «@» o el «://» con IndexOf antes de recorrer el bucle, así que el coste es lineal y no hay entrada que agote
    // los 100 ms sin depender de la máquina. Que MensajeSinDatos pase por este método lo fija el test de Architecture.
    [Fact]
    public void Si_la_redaccion_agota_su_tiempo_el_mensaje_sale_vacio()
    {
        var resultado = PrivacidadDeSentry.RedactarConFalloCerrado(
            "Buzón " + Correo, _ => throw new System.Text.RegularExpressions.RegexMatchTimeoutException());

        resultado.Should().BeEmpty("fallo cerrado: un mensaje que no se pudo revisar a tiempo no sale");
    }

    [Fact]
    public void Si_la_redaccion_termina_el_mensaje_sale_redactado()
    {
        PrivacidadDeSentry.RedactarConFalloCerrado("Buzón " + Correo, m => m.Replace(Correo, "[correo]"))
            .Should().Be("Buzón [correo]", "control: sin tiempo agotado sale lo que devuelve la redacción");
    }

    [Fact]
    public void Un_mensaje_largo_sin_urls_ni_correos_sale_intacto_por_el_filtro_real()
    {
        // Control: el filtro real con sus dos expresiones no recorta un mensaje sin URL ni correo.
        var corto = new string('a', 1_000);
        var original = new Breadcrumb(corto, "default", null, "CaeManager.Infrastructure", BreadcrumbLevel.Info);

        var filtrada = PrivacidadDeSentry.FiltrarMigaDePan(original, new SentryHint());

        filtrada.Should().NotBeNull();
        filtrada!.Message.Should().Be(corto, "control: un mensaje sin URL ni correo no se recorta");
        filtrada.Category.Should().Be(original.Category);
        filtrada.Level.Should().Be(original.Level);
    }

    [Theory]
    // Miga HTTP con una ruta relativa: no es una URL, pero lleva el GUID; se queda con el método y el origen.
    [InlineData("http", "GET /v1/employee/" + Guid + "?correo=" + Correo, "GET https://api.dokify.net/")]
    [InlineData("http", "", "GET https://api.dokify.net/")]
    // Miga de log de HttpClient o de ASP.NET Core: la URL se queda con el esquema y el host, sin credenciales.
    [InlineData("System.Net.Http.HttpClient.Graph.LogicalHandler",
        "Sending HTTP request GET https://usuario:clave@graph.microsoft.com/v1.0/users/" + Correo + "/messages?$filter=id%20eq%20'" + Guid + "'",
        "Sending HTTP request GET https://graph.microsoft.com/")]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics",
        "Request starting HTTP/1.1 GET https://app.talveg.test:8443/empresas/" + Guid + "?q=" + Correo + " - - -",
        "Request starting HTTP/1.1 GET https://app.talveg.test:8443/ - - -")]
    // Un correo fuera de una URL se tacha; un GUID suelto se conserva (el Id del documento de un reintento de IA).
    [InlineData("CaeManager.Infrastructure", "Buzón " + Correo + " sincronizado", "Buzón [correo] sincronizado")]
    [InlineData(null, "Intento 2/3 falló para el documento " + Guid, "Intento 2/3 falló para el documento " + Guid)]
    public void La_miga_de_pan_sale_sin_urls_consultas_ni_correos_en_su_mensaje(string? categoria, string mensaje, string esperado)
    {
        var datos = categoria == "http"
            ? new Dictionary<string, string>
            {
                ["url"] = "https://api.dokify.net/v1/employee/" + Guid + "?correo=" + Correo,
                ["method"] = "GET",
                ["status_code"] = "200",
            }
            : null;
        var original = new Breadcrumb(mensaje, categoria ?? "default", datos, categoria, BreadcrumbLevel.Info);

        var filtrada = PrivacidadDeSentry.FiltrarMigaDePan(original, new SentryHint());

        filtrada.Should().NotBeNull();
        filtrada!.Message.Should().Be(esperado);
        filtrada.Timestamp.Should().BeCloseTo(original.Timestamp, TimeSpan.FromSeconds(5));
        filtrada.Category.Should().Be(original.Category);
        filtrada.Level.Should().Be(original.Level);
        if (categoria == "http")
            filtrada.Data.Should().Equal(new Dictionary<string, string>
            {
                ["url"] = "https://api.dokify.net/",
                ["method"] = "GET",
                ["status_code"] = "200",
            }, "la miga HTTP sin marcar se queda con el origen, el método y el código");
        else
            filtrada.Data.Should().BeSameAs(original.Data, "fuera de una miga HTTP, los datos no se tocan");
    }

    [Theory]
    // Con credenciales y un puerto no estándar: el origen conserva el puerto y pierde las credenciales.
    [InlineData("https://usuario:clave@graph.microsoft.com:8443/v1.0/users/" + Correo + "/messages?$top=1", "https://graph.microsoft.com:8443/")]
    // Una URL que no se puede interpretar sale tachada (fallo cerrado).
    [InlineData("/v1.0/users/" + Correo, "[url]")]
    public void La_miga_http_se_queda_con_el_origen_y_descarta_otros_datos(string url, string esperada)
    {
        var original = new Breadcrumb(string.Empty, "http", new Dictionary<string, string>
        {
            ["url"] = url,
            ["method"] = "POST",
            ["status_code"] = "503",
            ["otro"] = Correo,
        }, "http", BreadcrumbLevel.Info);

        var filtrada = PrivacidadDeSentry.FiltrarMigaDePan(original, new SentryHint());

        filtrada!.Data.Should().Equal(new Dictionary<string, string>
        {
            ["url"] = esperada,
            ["method"] = "POST",
            ["status_code"] = "503",
        });
        filtrada.Type.Should().Be("http");
    }

    [Fact]
    public void El_evento_se_queda_con_el_origen_de_su_peticion_y_del_referer_sin_consulta_y_conserva_metodo_y_cabeceras()
    {
        var evento = new SentryEvent();
        evento.Request.Url = "https://app.talveg.test:8443/empresas/" + Guid;
        evento.Request.QueryString = "?q=" + Correo;
        evento.Request.Method = "GET";
        evento.Request.Headers["referer"] = "https://app.talveg.test:8443/buzones/" + Correo;
        evento.Request.Headers["Accept"] = "text/html";

        var filtrado = PrivacidadDeSentry.FiltrarEvento(evento);

        filtrado.Request.Url.Should().Be("https://app.talveg.test:8443/");
        filtrado.Request.QueryString.Should().BeNullOrEmpty();
        filtrado.Request.Method.Should().Be("GET");
        filtrado.Request.Headers.Should().Equal(new Dictionary<string, string>
        {
            ["referer"] = "https://app.talveg.test:8443/",
            ["Accept"] = "text/html",
        });

        var relativa = new SentryEvent();
        relativa.Request.Url = "/empresas/" + Guid;
        PrivacidadDeSentry.FiltrarEvento(relativa).Request.Url.Should().Be("[url]", "fallo cerrado: una URL que no se interpreta no sale");
    }

    [Fact]
    public async Task Con_la_configuracion_real_las_migas_del_sdk_salen_sin_urls_ni_correos_en_su_mensaje()
    {
        List<Breadcrumb> migas = [];
        using (IniciarSdk(PrivacidadDeSentry.Configurar))
        {
            SentrySdk.AddBreadcrumb(
                "Sending HTTP request GET https://graph.microsoft.com/v1.0/users/" + Correo + "/messages?$top=1",
                "System.Net.Http.HttpClient.Graph.LogicalHandler");
            using var http = new HttpClient(new SentryHttpMessageHandler(new AnotaCabeceras(new ConcurrentDictionary<string, string[]>())));
            (await http.GetAsync("https://api.dokify.net/v1/employee/" + Guid + "?correo=" + Correo)).Dispose();
            SentrySdk.ConfigureScope(ambito => migas = [.. ambito.Breadcrumbs]);
        }

        migas.Select(m => m.Message).Should().Contain("Sending HTTP request GET https://graph.microsoft.com/")
            .And.Contain("GET https://api.dokify.net/", "control: el manejador real dejó su miga HTTP");
        migas.Select(m => m.Message ?? string.Empty).Should().NotContain(m => m.Contains(Correo) || m.Contains(Guid) || m.Contains('?'));
    }

    [Fact]
    public async Task Con_la_configuracion_real_la_miga_marcada_y_la_sin_marcar_se_quedan_con_el_origen_y_sin_ella_no()
    {
        const string nif = "TEST0002B";
        var configuradas = await MigasHttpAsync(PrivacidadDeSentry.Configurar, nif);
        var sinConfigurar = await MigasHttpAsync(_ => { }, nif);

        sinConfigurar.Select(m => m.Data?.GetValueOrDefault("url") ?? string.Empty)
            .Should().Contain(u => u.Contains("control.invalid/employee/" + nif), "control: sin la configuración, la miga sin marcar lleva la ruta");

        var urls = configuradas.Select(m => m.Data?.GetValueOrDefault("url") ?? string.Empty).ToList();
        urls.Should().Contain("https://control.invalid/", "la miga sin marcar se queda con el origen");
        urls.Should().Contain("https://app.dokify.net/", "la miga de dokify se queda con el esquema y el host");
        configuradas.Should().OnlyContain(m => m.Data != null && m.Data["method"] == "GET" && m.Data["status_code"] == "200",
            "el método y el código de estado se conservan");
        configuradas.SelectMany(m => m.Data!.Values.Append(m.Message ?? string.Empty)).Should().NotContain(v => v.Contains(nif));
    }

    [Fact]
    public async Task Con_la_configuracion_real_el_5xx_de_capture_failed_requests_sale_con_el_origen_y_sin_ella_con_la_ruta()
    {
        const string destino = "https://graph.microsoft.com:8443/v1.0/users/" + Correo + "/messages?$filter=id%20eq%20'" + Guid + "'";
        var (evento, json) = await EventoDe5xxAsync(PrivacidadDeSentry.Configurar, destino);
        var (control, jsonDelControl) = await EventoDe5xxAsync(_ => { }, destino);

        control.Url.Should().Contain("/v1.0/users/" + Correo, "control: sin la configuración, el evento lleva la ruta");
        control.QueryString.Should().Contain("$filter", "control: sin la configuración, el evento lleva la consulta");
        jsonDelControl.Should().Contain(Correo, "control: el JSON que se enviaría lleva el correo de la ruta");

        evento.Url.Should().Be("https://graph.microsoft.com:8443/");
        evento.QueryString.Should().BeNullOrEmpty();
        evento.Method.Should().Be("GET");
        evento.StatusCode.Should().Be(500);
        json.Should().NotContain(Correo).And.NotContain("v1.0/users").And.NotContain(Guid).And.NotContain("$filter",
            "nada de la ruta ni de la consulta llega en el JSON que se enviaría");
    }

    [Fact]
    public async Task Con_el_filtro_de_program_dentro_de_configurar_el_5xx_de_dokify_no_se_envia_y_el_de_otro_host_si()
    {
        var transporte = new TransporteQueAnota();
        using (IniciarSdk(o =>
               {
                   o.CaptureFailedRequests = true;
                   PrivacidadDeSentry.Configurar(o, TrazasSinIdentificadoresEnLaRuta.FiltrarEvento);
               }, transporte))
        {
            using var http = new HttpClient(new SentryHttpMessageHandler(new Responde500()));
            var cliente = new DokifyClient(http, Options.Create(new DokifyOptions()), NullLogger<DokifyClient>.Instance);
            await cliente.ObtenerDocumentosAsync(new CredencialApiConector("token-de-prueba"), ElementoDokify.Trabajador, "TEST0002B", CancellationToken.None);
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(10));
            transporte.Sobres.Should().BeEmpty("el filtro de Program.cs corre dentro del SetBeforeSend de Configurar");

            (await http.GetAsync("https://control.invalid/employee/TEST0002B/docs")).Dispose();
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(10));
        }

        transporte.Sobres.Should().ContainSingle("control: el 5xx sin marcar sí se envía");
    }

    private static async Task<List<Breadcrumb>> MigasHttpAsync(Action<SentryOptions> configurar, string nif)
    {
        List<Breadcrumb> migas = [];
        using (IniciarSdk(configurar))
        {
            using var http = new HttpClient(new SentryHttpMessageHandler(new AnotaCabeceras(new ConcurrentDictionary<string, string[]>())));
            var cliente = new DokifyClient(http, Options.Create(new DokifyOptions()), NullLogger<DokifyClient>.Instance);
            await cliente.ObtenerDocumentosAsync(new CredencialApiConector("token-de-prueba"), ElementoDokify.Trabajador, nif, CancellationToken.None);
            (await http.GetAsync("https://control.invalid/employee/" + nif + "/docs")).Dispose();
            SentrySdk.ConfigureScope(ambito => migas = [.. ambito.Breadcrumbs.Where(m => m.Category == "http")]);
        }

        return migas;
    }

    /// <summary>
    /// Un 5xx real a través del <see cref="SentryHttpMessageHandler"/> con <c>CaptureFailedRequests</c>: devuelve el
    /// evento que llega al transporte (el que se enviaría) y el JSON de su sobre.
    /// </summary>
    private static async Task<(EventoEnviado Evento, string Json)> EventoDe5xxAsync(Action<SentryOptions> configurar, string destino)
    {
        var transporte = new TransporteQueAnota();
        using (IniciarSdk(o =>
               {
                   o.CaptureFailedRequests = true;
                   configurar(o);
               }, transporte))
        {
            using var http = new HttpClient(new SentryHttpMessageHandler(new Responde500()));
            (await http.GetAsync(destino)).Dispose();
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(10));
        }

        var sobre = transporte.Sobres.Should().ContainSingle("el 5xx genera un único evento").Subject;
        using var flujo = new MemoryStream();
        await sobre.SerializeAsync(flujo, null);
        var json = System.Text.Encoding.UTF8.GetString(flujo.ToArray());

        // El sobre es una línea de cabecera y, por elemento, una de cabecera y otra de contenido: el evento es la que
        // lleva «request».
        var evento = json.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(linea => System.Text.Json.JsonDocument.Parse(linea).RootElement)
            .Single(raiz => raiz.ValueKind == System.Text.Json.JsonValueKind.Object && raiz.TryGetProperty("request", out _));
        var peticion = evento.GetProperty("request");
        return (new EventoEnviado(
            Texto(peticion, "url"), Texto(peticion, "query_string"), Texto(peticion, "method"),
            evento.GetProperty("contexts").GetProperty("response").GetProperty("status_code").GetInt32()), json);

        static string? Texto(System.Text.Json.JsonElement objeto, string clave) =>
            objeto.TryGetProperty(clave, out var valor) ? valor.GetString() : null;
    }

    private sealed record EventoEnviado(string? Url, string? QueryString, string? Method, int StatusCode);

    private static async Task<Dictionary<string, string[]>> CabecerasEnviadasAsync(Action<SentryOptions> configurar)
    {
        var cabeceras = new ConcurrentDictionary<string, string[]>();
        using (IniciarSdk(configurar))
        {
            using var http = new HttpClient(new SentryHttpMessageHandler(new AnotaCabeceras(cabeceras)));
            foreach (var destino in Destinos)
                (await http.GetAsync(destino)).Dispose();
        }

        return new Dictionary<string, string[]>(cabeceras);
    }

    /// <summary>
    /// Un <c>WebApplication</c> real (sin arrancar) con <c>UseSentry</c> como lo llama Program.cs y el DSN y el destino
    /// de la traza en la configuración; devuelve la lista de propagación que resuelve <c>IOptions</c>.
    /// </summary>
    private static async Task<List<string>> ListaDePropagacionDelHostAsync(Action<SentryOptions> configurar)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sentry:Dsn"] = "https://public@sentry.invalid/1",
            ["Sentry:TracePropagationTargets:0"] = "control.invalid",
        });
        builder.WebHost.UseSentry(o => configurar(o));

        await using var app = builder.Build();
        try
        {
            return [.. app.Services.GetRequiredService<IOptions<SentryAspNetCoreOptions>>().Value.TracePropagationTargets.Select(t => t.ToString()!)];
        }
        finally
        {
            // El host puede dejar iniciado el SDK global: se cierra para que no contamine a las demás pruebas de la colección.
            if (SentrySdk.IsEnabled)
                SentrySdk.Close();
        }
    }

    private static IDisposable IniciarSdk(Action<SentryOptions> configurar, TransporteQueAnota? transporte = null) =>
        SentrySdk.Init(o =>
        {
            o.Dsn = "https://clave@sentry.invalid/1";
            o.AutoSessionTracking = false;
            o.SendClientReports = false;
            o.ShutdownTimeout = TimeSpan.Zero;
            // Nada sale a la red: lo que pasa el SetBeforeSend de Configurar llega a este transporte, que solo lo anota.
            o.Transport = transporte ?? new TransporteQueAnota();
            configurar(o);
        });

    private sealed class TransporteQueAnota : Sentry.Extensibility.ITransport
    {
        public ConcurrentQueue<Sentry.Protocol.Envelopes.Envelope> Sobres { get; } = new();

        public Task SendEnvelopeAsync(Sentry.Protocol.Envelopes.Envelope envelope, CancellationToken cancellationToken = default)
        {
            Sobres.Enqueue(envelope);
            return Task.CompletedTask;
        }
    }

    private sealed class Responde500 : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = request });
    }

    /// <summary>El destino: anota los nombres de las cabeceras de cada petición y contesta 200.</summary>
    private sealed class AnotaCabeceras(ConcurrentDictionary<string, string[]> anotadas) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            anotadas[request.RequestUri!.ToString()] = [.. request.Headers.Select(c => c.Key.ToLowerInvariant())];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
        }
    }
}
