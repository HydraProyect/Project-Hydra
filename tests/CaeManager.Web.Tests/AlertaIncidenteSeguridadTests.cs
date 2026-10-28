using System.Net;
using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Application.Seguridad.Incidentes;
using CaeManager.Infrastructure.Seguridad.Incidentes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests;

public class AlertaIncidenteSeguridadTests
{
    private static readonly Guid Incidente = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task El_canal_HTTP_solo_envia_el_identificador_y_el_enlace()
    {
        var manejador = new ManejadorGrabador(HttpStatusCode.OK);
        var canal = new CanalAlertaIncidenteSeguridadHttp(new FabricaClientes(manejador), Options.Create(new OpcionesIncidentesSeguridad
        {
            UrlAlertaSaliente = "https://alertas.example/heartbeat/fail",
            UrlBasePlataforma = "https://app.example",
        }));

        canal.Activo.Should().BeTrue();
        await canal.EnviarAsync(Incidente);

        manejador.Peticiones.Should().ContainSingle();
        var (metodo, uri, cuerpo) = manejador.Peticiones[0];
        metodo.Should().Be(HttpMethod.Post);
        uri.Should().Be(new Uri("https://alertas.example/heartbeat/fail"));

        using var json = JsonDocument.Parse(cuerpo);
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["incidenteId", "enlace"],
            "la alerta no lleva datos personales ni nombres de Tenant: solo el id y el enlace");
        json.RootElement.GetProperty("incidenteId").GetGuid().Should().Be(Incidente);
        json.RootElement.GetProperty("enlace").GetString().Should().Be("https://app.example/plataforma/incidentes/11111111-2222-3333-4444-555555555555");
    }

    [Fact]
    public async Task El_canal_HTTP_sin_URL_esta_desactivado_y_no_envia()
    {
        var manejador = new ManejadorGrabador(HttpStatusCode.OK);
        var canal = new CanalAlertaIncidenteSeguridadHttp(new FabricaClientes(manejador), Options.Create(new OpcionesIncidentesSeguridad()));

        canal.Activo.Should().BeFalse();
        await canal.EnviarAsync(Incidente);
        manejador.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_estado_de_error_lanza_para_que_la_cola_reintente()
    {
        var canal = new CanalAlertaIncidenteSeguridadHttp(new FabricaClientes(new ManejadorGrabador(HttpStatusCode.ServiceUnavailable)),
            Options.Create(new OpcionesIncidentesSeguridad { UrlAlertaSaliente = "https://alertas.example/x" }));

        await FluentActions.Awaiting(() => canal.EnviarAsync(Incidente)).Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task La_cola_reintenta_y_si_el_canal_sigue_fallando_cae_a_la_alerta_operativa()
    {
        var canal = new CanalFalso { FallosAntesDeExito = int.MaxValue };
        var operativa = new AlertaOperativaFalsa();
        var esperas = new List<TimeSpan>();
        var cola = new ColaAlertasIncidenteSeguridad(canal, operativa, NullLogger<ColaAlertasIncidenteSeguridad>.Instance,
            (espera, _) => { esperas.Add(espera); return Task.CompletedTask; });

        (await cola.EnviarConReintentosAsync(Incidente, CancellationToken.None)).Should().BeFalse();

        canal.Intentos.Should().Be(ColaAlertasIncidenteSeguridad.EsperasEntreIntentos.Length + 1);
        esperas.Should().Equal(ColaAlertasIncidenteSeguridad.EsperasEntreIntentos);
        operativa.Mensajes.Should().ContainSingle().Which.Should().Contain(Incidente.ToString("D"))
            .And.Subject.As<string>().Length.Should().BeLessThan(200);
    }

    [Fact]
    public async Task La_cola_para_al_primer_envio_correcto()
    {
        var canal = new CanalFalso { FallosAntesDeExito = 1 };
        var operativa = new AlertaOperativaFalsa();
        var cola = new ColaAlertasIncidenteSeguridad(canal, operativa, NullLogger<ColaAlertasIncidenteSeguridad>.Instance,
            (_, _) => Task.CompletedTask);

        (await cola.EnviarConReintentosAsync(Incidente, CancellationToken.None)).Should().BeTrue();
        canal.Intentos.Should().Be(2);
        operativa.Mensajes.Should().BeEmpty();
    }

    [Fact]
    public void Con_el_canal_desactivado_encolar_no_lanza_y_avisa_por_la_alerta_operativa()
    {
        var operativa = new AlertaOperativaFalsa();
        var cola = new ColaAlertasIncidenteSeguridad(new CanalAlertaIncidenteSeguridadNulo(), operativa,
            NullLogger<ColaAlertasIncidenteSeguridad>.Instance);

        FluentActions.Invoking(() => cola.Encolar(Incidente)).Should().NotThrow();
        operativa.Mensajes.Should().ContainSingle().Which.Should().Contain(Incidente.ToString("D"));
    }

    private sealed class CanalFalso : ICanalAlertaIncidenteSeguridad
    {
        public int FallosAntesDeExito { get; init; }
        public int Intentos { get; private set; }
        public bool Activo => true;

        public Task EnviarAsync(Guid incidenteId, CancellationToken ct = default)
        {
            Intentos++;
            return Intentos <= FallosAntesDeExito ? throw new HttpRequestException("canal caído") : Task.CompletedTask;
        }
    }

    private sealed class AlertaOperativaFalsa : IAlertaOperativa
    {
        public List<string> Mensajes { get; } = [];
        public void Emitir(string mensaje, NivelAlertaOperativa nivel) => Mensajes.Add(mensaje);
        public void CapturarExcepcion(Exception excepcion) { }
        public void DejarMigaDePan(string mensaje) { }
        public IDisposable IniciarAmbitoDeCaptura() => new Nada();

        private sealed class Nada : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class FabricaClientes(HttpMessageHandler manejador) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(manejador, disposeHandler: false);
    }

    private sealed class ManejadorGrabador(HttpStatusCode estado) : HttpMessageHandler
    {
        public List<(HttpMethod Metodo, Uri Uri, string Cuerpo)> Peticiones { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var cuerpo = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Peticiones.Add((request.Method, request.RequestUri!, cuerpo));
            return new HttpResponseMessage(estado);
        }
    }
}
