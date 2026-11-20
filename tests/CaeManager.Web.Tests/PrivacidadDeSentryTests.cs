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

        control.Should().Equal(["control.invalid"], "control: sin Configurar, el enlace sustituye el valor por defecto; si no corriera, la lista seria la de por defecto");
        enlazada.Should().BeEmpty();
        // Que el SDK global quede cerrado para el resto de la colección lo garantiza el finally de ListaDePropagacionDelHostAsync.
    }

    [Fact]
    public void Si_la_expresion_regular_agota_su_tiempo_la_miga_sale_con_el_mensaje_vacio()
    {
        // Sin correos ni URLs, pero con una sola «palabra» enorme: la expresión del correo prueba cada posición y recorre
        // hasta el final, cuadrático; con un tope de 100 ms agota su tiempo con margen de varios órdenes de magnitud.
        var mensaje = new string('a', 1_000_000);
        var original = new Breadcrumb(mensaje, "default", null, "CaeManager.Infrastructure", BreadcrumbLevel.Info);

        // Control: el mismo camino, con la misma categoria y nivel, y un mensaje corto sin URL ni correo sale intacto;
        // asi lo que vacia el mensaje enorme es el timeout y no un recorte incondicional.
        var corto = new string('a', 1_000);
        var cortoFiltrada = PrivacidadDeSentry.FiltrarMigaDePan(
            new Breadcrumb(corto, "default", null, "CaeManager.Infrastructure", BreadcrumbLevel.Info), new SentryHint());
        cortoFiltrada.Should().NotBeNull();
        cortoFiltrada!.Message.Should().Be(corto, "control: un mensaje corto sin URL ni correo no se recorta");

        var filtrada = PrivacidadDeSentry.FiltrarMigaDePan(original, new SentryHint());

        filtrada.Should().NotBeNull("la miga se conserva; lo que no sale es un mensaje que no se pudo revisar");
        // Longitud y no el texto: si falla, el mensaje de FluentAssertions no vuelca un megabyte.
        (filtrada!.Message ?? "x").Length.Should().Be(0, "fallo cerrado: el timeout de la expresión no deja salir el mensaje original");
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
        filtrada.Data.Should().BeSameAs(original.Data, "fuera de una petición marcada, los datos no se tocan");
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
    public async Task Con_la_configuracion_real_la_miga_de_dokify_sigue_perdiendo_la_ruta_y_la_misma_ruta_sin_marcar_no()
    {
        const string nif = "TEST0002B";
        List<Breadcrumb> migas = [];
        using (IniciarSdk(PrivacidadDeSentry.Configurar))
        {
            using var http = new HttpClient(new SentryHttpMessageHandler(new AnotaCabeceras(new ConcurrentDictionary<string, string[]>())));
            var cliente = new DokifyClient(http, Options.Create(new DokifyOptions()), NullLogger<DokifyClient>.Instance);
            await cliente.ObtenerDocumentosAsync(new CredencialApiConector("token-de-prueba"), ElementoDokify.Trabajador, nif, CancellationToken.None);
            (await http.GetAsync("https://control.invalid/employee/" + nif + "/docs")).Dispose();
            SentrySdk.ConfigureScope(ambito => migas = [.. ambito.Breadcrumbs]);
        }

        var urls = migas.Select(m => m.Data?.GetValueOrDefault("url") ?? string.Empty).ToList();
        urls.Should().Contain(u => u.Contains("control.invalid/employee/" + nif), "control: la miga sin marcar conserva la ruta");
        urls.Should().Contain("https://app.dokify.net/", "la miga de dokify se queda con el esquema y el host");
        migas.Where(m => m.Data?.GetValueOrDefault("url")?.Contains("dokify") == true)
            .SelectMany(m => m.Data!.Values.Append(m.Message ?? string.Empty)).Should().NotContain(v => v.Contains(nif));
    }

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

    private static IDisposable IniciarSdk(Action<SentryOptions> configurar) =>
        SentrySdk.Init(o =>
        {
            o.Dsn = "https://clave@sentry.invalid/1";
            o.AutoSessionTracking = false;
            o.SendClientReports = false;
            o.ShutdownTimeout = TimeSpan.Zero;
            // Nada sale a la red: la prueba solo mira lo que el SDK pone en la petición y en el ámbito.
            o.SetBeforeSend((_, _) => null);
            configurar(o);
        });

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
