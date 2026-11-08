using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Application.Contacto;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Email;
using CaeManager.Web.Features.Contacto;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// El endpoint del formulario de demo atravesando un pipeline HTTP de verdad
/// (Kestrel en loopback): CORS, limitador, páginas de estado y guarda de envío
/// solo se observan con middleware real, no invocando el manejador a mano.
///
/// <para>
/// El pipeline es un espejo mínimo del de <c>Program.cs</c> (mismas extensiones
/// <see cref="SolicitudDemoEndpoints.AddSolicitudDemo"/> y
/// <see cref="SolicitudDemoEndpoints.MapSolicitudDemoEndpoints"/>, mismo orden
/// relativo de <c>UseStatusCodePagesWithReExecute</c> → <c>UseCors</c> →
/// <c>UseRateLimiter</c>). Que <c>Program.cs</c> las llame no lo prueba esta
/// clase. Ningún test envía correo: el <see cref="IEmailService"/> es un doble.
/// </para>
/// </summary>
public class SolicitudDemoEndpointsTests
{
    private const string OrigenTalveg = "https://talveg.es";

    private static object CuerpoBueno(string? sitioWeb = null) => new
    {
        nombre = "Ana Pérez",
        correo = "ana@empresa.es",
        telefono = "600112233",
        perfil = 2,
        tramo = 3,
        intereses = new[] { 1, 2 },
        otra = "",
        sitioWeb,
    };

    [Fact]
    public async Task El_preflight_desde_talveg_es_se_acepta_solo_para_post_json()
    {
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync();

        foreach (var origen in new[] { OrigenTalveg, "https://www.talveg.es" })
        {
            using var respuesta = await servidor.Cliente.SendAsync(Preflight(origen));

            respuesta.StatusCode.Should().Be(HttpStatusCode.NoContent);
            respuesta.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal(origen);
            respuesta.Headers.GetValues("Access-Control-Allow-Methods").Should().Equal("POST");
            respuesta.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse(
                "el formulario no manda cookies y no se le deben permitir");
        }
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://talveg.es")]
    [InlineData("https://talveg.es.evil.example")]
    [InlineData("null")]
    public async Task El_preflight_desde_otro_origen_no_recibe_permiso_cors(string origen)
    {
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync();

        using var respuesta = await servidor.Cliente.SendAsync(Preflight(origen));

        respuesta.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse(
            "ASVS 4.0.3 V14.5.3: lista cerrada de orígenes exactos, sin el origen null");
    }

    [Fact]
    public async Task Un_envio_bueno_manda_un_correo_a_info_con_respuesta_a_quien_escribe()
    {
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync();

        using var respuesta = await servidor.PostAsync(CuerpoBueno());

        respuesta.StatusCode.Should().Be(HttpStatusCode.Accepted);
        respuesta.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal(OrigenTalveg);
        respuesta.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await LeerJson(respuesta)).GetProperty("estado").GetString().Should().Be("recibida");

        var enviado = servidor.Correo.Enviados.Should().ContainSingle().Subject;
        enviado.Destinatario.Should().Be("info@talveg.es");
        enviado.ResponderA.Should().Be("ana@empresa.es");
        enviado.Asunto.Should().Be("Solicitud de demo desde talveg.es: Operador CAE externo");
        enviado.Cuerpo.Should().Contain(WebUtility.HtmlEncode("Ana Pérez")).And.Contain("600112233");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    public async Task Sin_origen_permitido_el_servidor_rechaza_aunque_cors_sea_cosa_del_navegador(string? origen)
    {
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync();

        using var respuesta = await servidor.PostAsync(CuerpoBueno(), origen: origen);

        respuesta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        servidor.Correo.Enviados.Should().BeEmpty();
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("application/json; charset=iso-8859-1")]
    public async Task Solo_acepta_json(string tipo)
    {
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync();

        using var respuesta = await servidor.PostCrudoAsync(JsonSerializer.Serialize(CuerpoBueno()), tipo);

        respuesta.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        servidor.Correo.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_cuerpo_demasiado_grande_se_corta_con_413()
    {
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync();
        var relleno = new string('a', SolicitudDemoEndpoints.MaximoBytes);

        using var respuesta = await servidor.PostCrudoAsync($"{{\"nombre\":\"{relleno}\"}}", "application/json");

        respuesta.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await LeerJson(respuesta)).GetProperty("error").GetString().Should().Be("demasiado-grande");
    }

    [Theory]
    [InlineData("{\"nombre\":\"Ana\",\"correo\":\"ana@empresa.es\",\"perfil\":1,\"tramo\":1,\"intereses\":[1],\"admin\":true}")]
    [InlineData("{\"nombre\":\"Ana\",\"nombre\":\"Otra\",\"correo\":\"ana@empresa.es\",\"perfil\":1,\"tramo\":1,\"intereses\":[1]}")]
    [InlineData("{\"nombre\":\"Ana\",\"perfil\":\"1\"}")]
    [InlineData("no es json")]
    [InlineData("")]
    public async Task Un_json_fuera_de_contrato_es_400_sin_detalle_interno(string cuerpo)
    {
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync();

        using var respuesta = await servidor.PostCrudoAsync(cuerpo, "application/json");

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var texto = await respuesta.Content.ReadAsStringAsync();
        texto.Should().Be("{\"error\":\"json-no-valido\"}",
            "ASVS 4.0.3 V7.4.1: ni el mensaje de System.Text.Json ni la ruta del fallo");
        servidor.Correo.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Los_campos_no_validos_se_nombran_y_no_se_envia_nada()
    {
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync();

        using var respuesta = await servidor.PostAsync(new { nombre = "A", correo = "no-es-correo", perfil = 9, tramo = 1, intereses = new[] { 1 } });

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await LeerJson(respuesta);
        json.GetProperty("error").GetString().Should().Be("datos-no-validos");
        json.GetProperty("campos").EnumerateArray().Select(c => c.GetString())
            .Should().BeEquivalentTo(["nombre", "correo", "perfil"]);
        servidor.Correo.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task El_campo_trampa_se_descarta_con_la_misma_respuesta_que_un_envio_bueno()
    {
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync();

        using var respuesta = await servidor.PostAsync(CuerpoBueno(sitioWeb: "https://spam.example"));

        respuesta.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await LeerJson(respuesta)).GetProperty("estado").GetString().Should().Be("recibida");
        servidor.Correo.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task El_limite_por_ip_corta_con_429_en_json_y_no_con_la_pagina_html_de_estado()
    {
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync(limitePorIp: 2);

        (await servidor.PostAsync(CuerpoBueno())).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await servidor.PostAsync(new { nombre = "x" })).StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "lo que no pasa la validación también gasta cupo de la IP");

        using var respuesta = await servidor.PostAsync(CuerpoBueno());

        respuesta.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        respuesta.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        (await respuesta.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"demasiadas-solicitudes\"}",
            "un 429 sin cuerpo lo reescribiría UseStatusCodePagesWithReExecute como la página HTML de la app");
        respuesta.Headers.RetryAfter.Should().NotBeNull();
        respuesta.Headers.CacheControl!.NoStore.Should().BeTrue();
        servidor.Correo.Enviados.Should().HaveCount(1);
    }

    [Fact]
    public async Task El_cupo_global_acota_los_correos_aunque_cada_ip_vaya_dentro_de_su_limite()
    {
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync(limiteGlobalPorHora: 1);

        (await servidor.PostAsync(CuerpoBueno())).StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var respuesta = await servidor.PostAsync(CuerpoBueno());

        respuesta.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        servidor.Correo.Enviados.Should().HaveCount(1);
    }

    [Fact]
    public async Task Con_la_guarda_de_correo_cerrada_contesta_503_y_no_sale_nada()
    {
        // Staging arranca como Production: lo que impide que salga correo allí es la guarda
        // (EnvioCorreo__Modo, cerrada por defecto), no el entorno.
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync(guardaCerrada: true);

        using var respuesta = await servidor.PostAsync(CuerpoBueno());

        respuesta.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await respuesta.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"no-disponible\"}");
        servidor.Correo.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_fallo_del_smtp_no_filtra_el_mensaje_interno()
    {
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync();
        servidor.Correo.Respuesta = Result.Fallo(Error.Crear("Email.ErrorRed", "mail.talveg.es:587 no responde"));

        using var respuesta = await servidor.PostAsync(CuerpoBueno());

        respuesta.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await respuesta.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"no-disponible\"}");
    }

    [Fact]
    public async Task Solo_admite_post()
    {
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync();

        using var respuesta = await servidor.Cliente.GetAsync(SolicitudDemoEndpoints.Ruta);

        respuesta.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    public void La_clave_del_limite_agrupa_ipv6_por_su_64(string ip, string clave)
    {
        SolicitudDemoEndpoints.ClaveIp(IPAddress.Parse(ip)).Should().Be(clave);
    }

    private static HttpRequestMessage Preflight(string origen)
    {
        var peticion = new HttpRequestMessage(HttpMethod.Options, SolicitudDemoEndpoints.Ruta);
        peticion.Headers.Add("Origin", origen);
        peticion.Headers.Add("Access-Control-Request-Method", "POST");
        peticion.Headers.Add("Access-Control-Request-Headers", "content-type");
        return peticion;
    }

    private static async Task<JsonElement> LeerJson(HttpResponseMessage respuesta) =>
        await respuesta.Content.ReadFromJsonAsync<JsonElement>();

    private sealed class ServidorSolicitudDemo : IAsyncDisposable
    {
        private readonly WebApplication app;

        private ServidorSolicitudDemo(WebApplication app, HttpClient cliente, CorreoFalso correo)
        {
            this.app = app;
            Cliente = cliente;
            Correo = correo;
        }

        public HttpClient Cliente { get; }

        public CorreoFalso Correo { get; }

        public static async Task<ServidorSolicitudDemo> ArrancarAsync(
            int limitePorIp = 50, int limiteGlobalPorHora = 50, bool guardaCerrada = false)
        {
            var correo = new CorreoFalso();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseKestrel(opciones => opciones.Listen(IPAddress.Loopback, 0));
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:SolicitudDemo:LimitePorIp"] = limitePorIp.ToString(),
                ["RateLimiting:SolicitudDemo:LimiteGlobalPorHora"] = limiteGlobalPorHora.ToString(),
            });
            builder.Services.AddSolicitudDemo(builder.Configuration);
            builder.Services.AddSingleton<IEmailService>(guardaCerrada
                ? new GuardaEnvioEmailService(correo, Options.Create(new EnvioCorreoOptions()), NullLogger<GuardaEnvioEmailService>.Instance)
                : correo);

            var app = builder.Build();
            app.UseStatusCodePagesWithReExecute("/not-found");
            app.UseCors();
            app.UseRateLimiter();
            app.MapGet("/not-found", () => Results.Content("<html>pagina de estado</html>", "text/html"));
            app.MapSolicitudDemoEndpoints();
            await app.StartAsync();

            var direccion = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new ServidorSolicitudDemo(app, new HttpClient { BaseAddress = new Uri(direccion) }, correo);
        }

        public Task<HttpResponseMessage> PostAsync(object cuerpo, string? origen = OrigenTalveg) =>
            PostCrudoAsync(JsonSerializer.Serialize(cuerpo), "application/json", origen);

        public Task<HttpResponseMessage> PostCrudoAsync(string cuerpo, string tipo, string? origen = OrigenTalveg)
        {
            var peticion = new HttpRequestMessage(HttpMethod.Post, SolicitudDemoEndpoints.Ruta)
            {
                Content = new StringContent(cuerpo, Encoding.UTF8),
            };
            peticion.Content.Headers.Remove("Content-Type");
            peticion.Content.Headers.TryAddWithoutValidation("Content-Type", tipo);
            if (origen is not null)
                peticion.Headers.Add("Origin", origen);
            return Cliente.SendAsync(peticion);
        }

        public async ValueTask DisposeAsync()
        {
            Cliente.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class CorreoFalso : IEmailService
    {
        public List<(string Destinatario, string Asunto, string Cuerpo, string? ResponderA)> Enviados { get; } = [];

        public Result Respuesta { get; set; } = Result.Exito();

        public Task<Result> EnviarAsync(
            string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo,
            string? responderA = null, CancellationToken cancellationToken = default)
        {
            if (Respuesta.EsExitoso)
                Enviados.Add((destinatarioEmail, asunto, cuerpoHtml, responderA));
            return Task.FromResult(Respuesta);
        }
    }
}
