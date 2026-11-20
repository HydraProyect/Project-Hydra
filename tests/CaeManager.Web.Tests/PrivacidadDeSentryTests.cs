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

    // Cabeceras que nunca salen: las de credenciales o sesión, las de IP del proxy, las de clave de un tercero y las
    // propias de la app (Tenant, ticket de entrega y revisión desplegada).
    private static readonly Dictionary<string, string> CabecerasProhibidas = new(StringComparer.Ordinal)
    {
        ["Cookie"] = "sesion=valor-cookie-secreto",
        ["authorization"] = "Bearer token-secreto-de-prueba",
        ["X-Forwarded-For"] = "203.0.113.7",
        ["x-real-ip"] = "198.51.100.9",
        ["X-Api-Key"] = "clave-api-secreta",
        ["Ocp-Apim-Subscription-Key"] = "clave-suscripcion-secreta",
        ["X-Csrf-Token"] = "token-csrf-secreto",
        ["X-Tenant-Beneficiario"] = Guid,
        ["X-Ticket-Entrega"] = "ticket-entrega-secreto",
        ["X-Talveg-Revision"] = "revision-secreta",
    };

    // Las de la lista blanca, con mayúsculas y minúsculas mezcladas: la comparación no las distingue.
    private static readonly Dictionary<string, string> CabecerasPermitidasMezcladas = new(StringComparer.Ordinal)
    {
        ["user-agent"] = "Mozilla/5.0 (prueba)",
        ["ACCEPT"] = "text/html",
        ["Accept-Language"] = "es-ES",
        ["content-TYPE"] = "application/json",
    };

    [Fact]
    public void El_evento_sale_solo_con_las_cabeceras_de_la_lista_blanca_sin_distinguir_mayusculas()
    {
        var evento = new SentryEvent();
        evento.Request.Url = "https://app.talveg.test/empresas/" + Guid;
        evento.Request.Cookies = "sesion=valor-cookie-secreto";
        foreach (var (nombre, valor) in CabecerasProhibidas.Concat(CabecerasPermitidasMezcladas))
            evento.Request.Headers[nombre] = valor;
        evento.Request.Headers["REFERER"] = "https://app.talveg.test/buzones/" + Correo;
        evento.Contexts[Sentry.Protocol.Response.Type] = new Sentry.Protocol.Response { StatusCode = 500 };
        evento.Contexts.Response.Cookies = "sesion=valor-cookie-secreto";
        evento.Contexts.Response.Headers["Set-Cookie"] = "sesion=valor-cookie-secreto";
        evento.Contexts.Response.Headers["x-tenant-efectivo"] = Guid;
        evento.Contexts.Response.Headers["Content-Type"] = "text/html";

        var filtrado = PrivacidadDeSentry.FiltrarEvento(evento);

        filtrado.Request.Headers.Should().Equal(new Dictionary<string, string>(CabecerasPermitidasMezcladas)
        {
            ["REFERER"] = "https://app.talveg.test/",
        });
        filtrado.Request.Cookies.Should().BeNull();
        filtrado.Contexts.Response.Headers.Should().Equal(new Dictionary<string, string> { ["Content-Type"] = "text/html" });
        filtrado.Contexts.Response.Cookies.Should().BeNull();
        filtrado.Contexts.Response.StatusCode.Should().Be(500, "el código de estado se conserva");

        var sinRespuesta = new SentryEvent();
        sinRespuesta.Request.Headers["Cookie"] = "sesion=valor-cookie-secreto";
        PrivacidadDeSentry.FiltrarEvento(sinRespuesta).Contexts.ContainsKey(Sentry.Protocol.Response.Type)
            .Should().BeFalse("el filtro no añade un contexto de respuesta a un evento que no lo traía");
    }

    [Fact]
    public async Task Con_la_configuracion_real_el_evento_de_la_peticion_del_ambito_sale_solo_con_la_lista_blanca_y_sin_ella_con_todo()
    {
        // Sentry.AspNetCore pone la petición entrante en el ámbito, y el ámbito la copia en cada evento: es la vía por la
        // que una cabecera de la petición llega al SetBeforeSend.
        var configurado = await JsonDeEventoConCabecerasEnElAmbitoAsync(PrivacidadDeSentry.Configurar);
        var control = await JsonDeEventoConCabecerasEnElAmbitoAsync(_ => { });

        // Control: sin la configuración, el SDK 6.7.0 solo tacha el valor de «authorization» ("[Filtered]") y manda el
        // resto, Cookie y X-Forwarded-For incluidas.
        control.Should().Contain("\"authorization\":\"[Filtered]\"");
        foreach (var (nombre, valor) in CabecerasProhibidas)
        {
            control.Should().Contain($"\"{nombre}\":", "control: sin la configuración, el JSON que se enviaría lleva la cabecera");
            configurado.Should().NotContain($"\"{nombre}\":").And.NotContain(valor);
        }

        control.Should().Contain("valor-cookie-secreto", "control: sin la configuración, las cookies del ámbito salen");
        configurado.Should().NotContain("valor-cookie-secreto");
        foreach (var valor in CabecerasProhibidas.Values.Where(v => v != CabecerasProhibidas["authorization"]))
            control.Should().Contain(valor, "control: sin la configuración, el valor sale tal cual");

        foreach (var (nombre, valor) in CabecerasPermitidasMezcladas)
            configurado.Should().Contain(nombre).And.Contain(valor, "la cabecera de la lista blanca se conserva");
    }

    [Fact]
    public async Task Aunque_la_configuracion_encienda_trazas_logs_y_pii_la_configuracion_real_los_apaga_y_sin_ella_no()
    {
        // Como lo traerían appsettings o las variables de entorno Sentry__TracesSampleRate, Sentry__EnableLogs y
        // Sentry__SendDefaultPii; el muestreador, como lo pondría una línea de Program.cs anterior a Configurar.
        var configuracion = new Dictionary<string, string?>
        {
            ["Sentry:TracesSampleRate"] = "1.0",
            ["Sentry:EnableLogs"] = "true",
            ["Sentry:SendDefaultPii"] = "true",
        };
        static double? Muestrea(TransactionSamplingContext _) => 1.0;

        var control = await OpcionesDelHostAsync(o => o.TracesSampler = Muestrea, configuracion);
        control.TracesSampleRate.Should().Be(1.0, "control: el enlace de la sección «Sentry» enciende las trazas");
        control.EnableLogs.Should().BeTrue("control: el enlace enciende los logs");
        control.SendDefaultPii.Should().BeTrue("control: el enlace enciende los datos personales por defecto");
        control.TracesSampler.Should().NotBeNull();

        var opciones = await OpcionesDelHostAsync(o =>
        {
            o.TracesSampler = Muestrea;
            PrivacidadDeSentry.Configurar(o);
        }, configuracion);

        opciones.TracesSampleRate.Should().BeNull("sin transacciones, que no pasan por SetBeforeSend");
        opciones.TracesSampler.Should().BeNull();
        opciones.EnableLogs.Should().BeFalse("sin logs, que no pasan por SetBeforeSend");
        opciones.SendDefaultPii.Should().BeFalse();
    }

    [Fact]
    public void Si_un_filtro_lanza_el_descarte_se_cuenta_solo_con_el_tipo_y_la_excepcion_y_si_termina_no()
    {
        var medidas = new ConcurrentQueue<(long Valor, KeyValuePair<string, object?>[] Etiquetas)>();
        using var oyente = new System.Diagnostics.Metrics.MeterListener();
        oyente.InstrumentPublished = (instrumento, o) =>
        {
            if (instrumento.Meter.Name == CaeManager.Application.Common.Observabilidad.NombreOrigen
                && instrumento.Name == PrivacidadDeSentry.NombreMetricaDescartes)
                o.EnableMeasurementEvents(instrumento);
        };
        oyente.SetMeasurementEventCallback<long>((_, valor, etiquetas, _) => medidas.Enqueue((valor, etiquetas.ToArray())));
        oyente.Start();

        const string url = "https://graph.microsoft.com/v1.0/users/" + Correo + "/messages?$top=1";
        const string mensaje = "Mensaje del evento para " + Correo;
        SentryEvent Evento()
        {
            var evento = new SentryEvent { Message = mensaje };
            evento.Request.Url = url;
            return evento;
        }

        PrivacidadDeSentry.FiltrarEventoConFalloCerrado(Evento(), new SentryHint(), (e, _) => e).Should().NotBeNull();
        PrivacidadDeSentry.FiltrarMigaDePanConFalloCerrado(new Breadcrumb(mensaje, "default"), new SentryHint(), PrivacidadDeSentry.FiltrarMigaDePan)
            .Should().NotBeNull();
        medidas.Should().BeEmpty("control: sin excepción no hay descarte que contar");

        PrivacidadDeSentry.FiltrarEventoConFalloCerrado(Evento(), new SentryHint(), (_, _) => throw new InvalidOperationException("roto en " + url + " " + mensaje))
            .Should().BeNull("fallo cerrado: el evento no se envía");
        PrivacidadDeSentry.FiltrarMigaDePanConFalloCerrado(new Breadcrumb(mensaje, "default"), new SentryHint(), (_, _) => throw new ArgumentException("roto en " + url))
            .Should().BeNull("fallo cerrado: la miga no se guarda");

        medidas.Select(m => (m.Valor, Etiquetas: string.Join(";", m.Etiquetas.Select(e => $"{e.Key}={e.Value}"))))
            .Should().Equal(
                (1L, "tipo=evento;excepcion=InvalidOperationException"),
                (1L, "tipo=miga;excepcion=ArgumentException"));
        medidas.SelectMany(m => m.Etiquetas).Select(e => e.Value?.ToString() ?? string.Empty)
            .Should().NotContain(v => v.Contains(Correo) || v.Contains("graph.microsoft.com") || v.Contains("Mensaje") || v.Contains("roto"),
                "la señal no lleva la URL, el mensaje ni nada del evento o de la excepción");
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

    [Fact]
    public void Si_el_filtro_de_eventos_lanza_el_evento_no_se_envia()
    {
        var evento = new SentryEvent();
        evento.Request.Url = "https://graph.microsoft.com/v1.0/users/" + Correo;

        PrivacidadDeSentry.FiltrarEventoConFalloCerrado(evento, new SentryHint(), (_, _) => throw new InvalidOperationException())
            .Should().BeNull("fallo cerrado: un evento que no se pudo filtrar no sale");
    }

    [Fact]
    public void Si_el_filtro_de_eventos_termina_el_evento_sale_sin_la_ruta()
    {
        var evento = new SentryEvent();
        evento.Request.Url = "https://graph.microsoft.com/v1.0/users/" + Correo;

        PrivacidadDeSentry.FiltrarEventoConFalloCerrado(evento, new SentryHint(), (e, _) => e)!
            .Request.Url.Should().Be("https://graph.microsoft.com/", "control: sin excepción sale el evento filtrado");
    }

    [Fact]
    public void Si_el_filtro_de_migas_lanza_la_miga_se_descarta()
    {
        var miga = new Breadcrumb("Buzón " + Correo, "default");

        PrivacidadDeSentry.FiltrarMigaDePanConFalloCerrado(miga, new SentryHint(), (_, _) => throw new InvalidOperationException())
            .Should().BeNull("fallo cerrado: una miga que no se pudo filtrar no se guarda");
    }

    [Fact]
    public void Si_el_filtro_de_migas_termina_la_miga_sale_filtrada()
    {
        var miga = new Breadcrumb("Buzón " + Correo, "default");

        PrivacidadDeSentry.FiltrarMigaDePanConFalloCerrado(miga, new SentryHint(), PrivacidadDeSentry.FiltrarMigaDePan)!
            .Message.Should().Be("Buzón [correo]", "control: sin excepción sale lo que devuelve el filtro real");
    }

    [Fact]
    public async Task Con_la_configuracion_real_si_el_filtro_de_program_lanza_el_5xx_no_se_envia_y_sin_ella_sale_con_la_ruta()
    {
        const string destino = "https://graph.microsoft.com/v1.0/users/" + Correo + "/messages";
        static SentryEvent? Lanza(SentryEvent evento, SentryHint pista) => throw new InvalidOperationException("filtro roto");

        var configurado = await SobresDe5xxAsync(o => PrivacidadDeSentry.Configurar(o, Lanza), destino);
        var control = await SobresDe5xxAsync(o => o.SetBeforeSend(Lanza), destino);

        // Control: con un SetBeforeSend que lanza, el SDK envía el evento original, con la ruta de su petición.
        var sobre = control.Should().ContainSingle("control: el SDK envía el evento aunque su BeforeSend lance").Subject;
        using var flujo = new MemoryStream();
        await sobre.SerializeAsync(flujo, null);
        System.Text.Encoding.UTF8.GetString(flujo.ToArray()).Should().Contain("/v1.0/users/" + Correo);

        configurado.Should().BeEmpty("fallo cerrado: el SetBeforeSend de Configurar descarta el evento que no pudo filtrar");
    }

    [Fact]
    public void Sin_la_configuracion_si_el_filtro_de_migas_lanza_el_sdk_descarta_la_miga_y_si_termina_la_guarda()
    {
        // Lo que hace el SDK sin FiltrarMigaDePanConFalloCerrado: hoy ya descarta la miga cuyo BeforeBreadcrumb lanza, y
        // el fallo cerrado propio lo mantiene aunque una versión futura cambie ese comportamiento. Configurar no admite
        // otro filtro de migas, así que su cableado lo fija el test de Architecture.
        static List<Breadcrumb> Migas(Func<Breadcrumb, SentryHint, Breadcrumb?> filtro)
        {
            List<Breadcrumb> migas = [];
            using (IniciarSdk(o => o.SetBeforeBreadcrumb(filtro)))
            {
                SentrySdk.AddBreadcrumb("Buzón " + Correo, "prueba");
                SentrySdk.ConfigureScope(ambito => migas = [.. ambito.Breadcrumbs.Where(m => m.Category == "prueba")]);
            }

            return migas;
        }

        Migas((miga, _) => miga).Select(m => m.Message).Should().Contain("Buzón " + Correo, "control: con un filtro que termina, el SDK guarda la miga");
        Migas((_, _) => throw new InvalidOperationException("filtro roto")).Should().BeEmpty("el SDK descarta la miga si su BeforeBreadcrumb lanza");
    }

    private static async Task<List<Sentry.Protocol.Envelopes.Envelope>> SobresDe5xxAsync(Action<SentryOptions> configurar, string destino)
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

        return [.. transporte.Sobres];
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

    /// <summary>
    /// Un evento capturado con la petición, sus cookies y todas las cabeceras de prueba en el ámbito, como las deja
    /// Sentry.AspNetCore: devuelve el JSON del único sobre que llega al transporte (el que se enviaría).
    /// </summary>
    private static async Task<string> JsonDeEventoConCabecerasEnElAmbitoAsync(Action<SentryOptions> configurar)
    {
        var transporte = new TransporteQueAnota();
        using (IniciarSdk(configurar, transporte))
        {
            SentrySdk.ConfigureScope(ambito =>
            {
                ambito.Request.Url = "https://app.talveg.test/empresas";
                ambito.Request.Method = "POST";
                ambito.Request.Cookies = "sesion=valor-cookie-secreto";
                foreach (var (nombre, valor) in CabecerasProhibidas.Concat(CabecerasPermitidasMezcladas))
                    ambito.Request.Headers[nombre] = valor;
            });
            SentrySdk.CaptureException(new InvalidOperationException("fallo de prueba"));
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(10));
        }

        var sobre = transporte.Sobres.Should().ContainSingle("la excepción genera un único evento").Subject;
        using var flujo = new MemoryStream();
        await sobre.SerializeAsync(flujo, null);
        return System.Text.Encoding.UTF8.GetString(flujo.ToArray());
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
        var opciones = await OpcionesDelHostAsync(configurar, new Dictionary<string, string?>
        {
            ["Sentry:TracePropagationTargets:0"] = "control.invalid",
        });
        return [.. opciones.TracePropagationTargets.Select(t => t.ToString()!)];
    }

    /// <summary>
    /// Un <c>WebApplication</c> real (sin arrancar) con <c>UseSentry</c> como lo llama Program.cs, el DSN y
    /// <paramref name="configuracion"/> en la sección «Sentry»; devuelve las opciones que resuelve <c>IOptions</c>.
    /// </summary>
    private static async Task<SentryAspNetCoreOptions> OpcionesDelHostAsync(
        Action<SentryOptions> configurar, Dictionary<string, string?> configuracion)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(configuracion)
        {
            ["Sentry:Dsn"] = "https://public@sentry.invalid/1",
        });
        builder.WebHost.UseSentry(o => configurar(o));

        await using var app = builder.Build();
        try
        {
            return app.Services.GetRequiredService<IOptions<SentryAspNetCoreOptions>>().Value;
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
