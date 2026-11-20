using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CaeManager.Application.Integraciones.ConectoresPlataformaCae;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.Dokify;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Sentry;
using Xunit;

namespace CaeManager.Web.Tests;

[CollectionDefinition(Nombre, DisableParallelization = true)]
public sealed class ColeccionInstrumentacionGlobal
{
    /// <summary>OpenTelemetry escucha todo HttpClient del proceso y SentrySdk es estático: estas pruebas corren solas.</summary>
    public const string Nombre = "Instrumentación global de HTTP saliente";
}

/// <summary>
/// Que una petición del cliente HTTP de dokify, con un CIF o un NIF en la ruta, no exporte esa ruta ni a OpenTelemetry
/// (span con <c>url.full</c> hacia Seq) ni a Sentry (miga de pan con la URL, evento de un 5xx). Con la instrumentación
/// real: OpenTelemetry sobre un <c>SocketsHttpHandler</c> contra un servidor HTTP local, y Sentry con su
/// <see cref="SentryHttpMessageHandler"/> y el SDK iniciado. Cada prueba lleva su control: la misma ruta sin marcar sí se
/// exporta, así que el instrumento ve lo que se busca.
/// </summary>
[Collection(ColeccionInstrumentacionGlobal.Nombre)]
public class TrazasSinIdentificadoresEnLaRutaTests
{
    private const string Nif = "TEST0002B";
    private static readonly CredencialApiConector Credencial = new("token-de-prueba");

    [Fact]
    public async Task OpenTelemetry_no_traza_la_peticion_de_dokify_y_si_traza_la_misma_ruta_sin_marcar()
    {
        using var servidor = new ServidorHttpLocal();
        var trazadas = new ConcurrentQueue<Activity>();
        using var proveedor = Sdk.CreateTracerProviderBuilder()
            .AddHttpClientInstrumentation(TrazasSinIdentificadoresEnLaRuta.ConfigurarInstrumentacionHttp)
            .AddProcessor(new Recolector(trazadas))
            .Build();
        var etiquetasDeMetricas = new ConcurrentQueue<string>();
        using var oyente = new MeterListener();
        oyente.InstrumentPublished = (instrumento, l) =>
        {
            if (instrumento.Meter.Name.StartsWith("System.Net.Http", StringComparison.Ordinal))
                l.EnableMeasurementEvents(instrumento);
        };
        oyente.SetMeasurementEventCallback<double>((_, _, etiquetas, _) => Anotar(etiquetasDeMetricas, etiquetas));
        oyente.SetMeasurementEventCallback<long>((_, _, etiquetas, _) => Anotar(etiquetasDeMetricas, etiquetas));
        oyente.Start();

        using var http = new HttpClient(new SocketsHttpHandler());
        var cliente = new DokifyClient(http, Options.Create(new DokifyOptions { UrlBase = servidor.UrlBase }), NullLogger<DokifyClient>.Instance);

        var resultado = await cliente.ObtenerDocumentosAsync(Credencial, ElementoDokify.Trabajador, Nif, CancellationToken.None);
        (await http.GetAsync(servidor.UrlBase + "control/" + Nif)).Dispose();
        proveedor.ForceFlush();

        resultado.Fallo.Should().BeNull("el servidor local contestó a la petición de dokify");
        var textos = trazadas.Select(a => a.DisplayName + " " + string.Join(' ', a.TagObjects.Select(t => t.Value))).ToList();
        textos.Should().Contain(t => t.Contains("control/" + Nif), "control: una petición sin marcar se traza con su ruta");
        textos.Should().NotContain(t => t.Contains("employee/" + Nif), "la petición de dokify no se traza");
        etiquetasDeMetricas.Should().NotBeEmpty("control: las métricas HTTP se midieron");
        etiquetasDeMetricas.Should().NotContain(e => e.Contains(Nif), "las métricas HTTP no llevan la ruta");
    }

    [Fact]
    public async Task Sentry_no_envia_el_5xx_de_dokify_y_su_miga_pierde_la_ruta_y_si_lo_hace_con_la_misma_ruta_sin_marcar()
    {
        var eventos = new ConcurrentQueue<SentryEvent>();
        var migas = new ConcurrentQueue<Breadcrumb>();
        MotivoUsoConector? fallo;
        using (SentrySdk.Init(o =>
               {
                   o.Dsn = "https://clave@sentry.invalid/1";
                   o.CaptureFailedRequests = true;
                   o.AutoSessionTracking = false;
                   o.SendClientReports = false;
                   o.ShutdownTimeout = TimeSpan.Zero;
                   // Lo que pasa el filtro se anota y no se envía: la prueba no sale a la red.
                   o.SetBeforeSend((evento, pista) =>
                   {
                       if (TrazasSinIdentificadoresEnLaRuta.FiltrarEvento(evento, pista) is { } enviado)
                           eventos.Enqueue(enviado);
                       return null;
                   });
                   o.SetBeforeBreadcrumb((miga, pista) =>
                   {
                       var guardada = TrazasSinIdentificadoresEnLaRuta.FiltrarMigaDePan(miga, pista);
                       if (guardada is not null)
                           migas.Enqueue(guardada);
                       return guardada;
                   });
               }))
        {
            using var http = new HttpClient(new SentryHttpMessageHandler(new Responde500()));
            var cliente = new DokifyClient(http, Options.Create(new DokifyOptions()), NullLogger<DokifyClient>.Instance);

            fallo = (await cliente.ObtenerDocumentosAsync(Credencial, ElementoDokify.Trabajador, Nif, CancellationToken.None)).Fallo;
            (await http.GetAsync("https://control.invalid/employee/" + Nif + "/docs")).Dispose();
        }

        fallo.Should().Be(MotivoUsoConector.PlataformaNoDisponible);
        var urlsDeEventos = eventos.Select(e => e.Request?.Url ?? string.Empty).ToList();
        urlsDeEventos.Should().Contain(u => u.Contains("control.invalid/employee/" + Nif), "control: un 5xx sin marcar sí se envía con su URL");
        urlsDeEventos.Should().NotContain(u => u.Contains("dokify"), "el 5xx de dokify no se envía");

        var urlsDeMigas = migas.Select(m => m.Data is { } d && d.TryGetValue("url", out var u) ? u : string.Empty).ToList();
        urlsDeMigas.Should().Contain(u => u.Contains("control.invalid/employee/" + Nif), "control: la miga sin marcar conserva la ruta");
        urlsDeMigas.Should().Contain("https://app.dokify.net/", "la miga de dokify se queda con el esquema y el host");
        migas.Where(m => m.Data?.GetValueOrDefault("url")?.Contains("dokify") == true)
            .SelectMany(m => m.Data!.Values).Should().NotContain(v => v.Contains(Nif));
    }

    private static void Anotar(ConcurrentQueue<string> cola, ReadOnlySpan<KeyValuePair<string, object?>> etiquetas)
    {
        foreach (var etiqueta in etiquetas)
            cola.Enqueue($"{etiqueta.Key}={etiqueta.Value}");
    }

    /// <summary>Lo que exportaría un exportador: solo las actividades grabadas.</summary>
    private sealed class Recolector(ConcurrentQueue<Activity> cola) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            if (data.Recorded)
                cola.Enqueue(data);
        }
    }

    private sealed class Responde500 : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = request });
    }

    /// <summary>Un servidor HTTP/1.1 mínimo en el bucle local que contesta <c>200 {}</c> a todo y cierra la conexión.</summary>
    private sealed class ServidorHttpLocal : IDisposable
    {
        private readonly TcpListener _escucha = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _fin = new();

        public ServidorHttpLocal()
        {
            _escucha.Start();
            _ = AtenderAsync();
        }

        public string UrlBase => $"http://127.0.0.1:{((IPEndPoint)_escucha.LocalEndpoint).Port}/app/";

        private async Task AtenderAsync()
        {
            while (!_fin.IsCancellationRequested)
            {
                TcpClient conexion;
                try
                {
                    conexion = await _escucha.AcceptTcpClientAsync(_fin.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => ContestarAsync(conexion));
            }
        }

        private static async Task ContestarAsync(TcpClient conexion)
        {
            using (conexion)
            {
                var flujo = conexion.GetStream();
                var buffer = new byte[8192];
                var leido = new StringBuilder();
                while (!leido.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var n = await flujo.ReadAsync(buffer);
                    if (n == 0)
                        return;
                    leido.Append(Encoding.ASCII.GetString(buffer, 0, n));
                }

                await flujo.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"));
            }
        }

        public void Dispose()
        {
            _fin.Cancel();
            _escucha.Stop();
            _fin.Dispose();
        }
    }
}
