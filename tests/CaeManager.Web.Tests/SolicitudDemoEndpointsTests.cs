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

    private static object CuerpoBueno(string? campoAdicional = null) => new
    {
        nombre = "Ana Pérez",
        correo = "ana@empresa.es",
        telefono = "600112233",
        perfil = 2,
        tramo = 3,
        intereses = new[] { 1, 2 },
        otra = "",
        campoAdicional,
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
        enviado.Destinatario.Should().Be("info@talveg.es", "sin ajuste, la dirección de reserva");
        enviado.ResponderA.Should().Be("ana@empresa.es");
        enviado.Asunto.Should().Be("Solicitud de demo desde talveg.es: Operador CAE externo");
        enviado.Cuerpo.Should().Contain(WebUtility.HtmlEncode("Ana Pérez")).And.Contain("600112233");
    }

    [Theory]
    [InlineData("demo@example.com", "demo@example.com")]
    [InlineData("  demo.ventas@example.com  ", "demo.ventas@example.com")]
    public async Task Con_el_ajuste_puesto_el_correo_va_a_esa_direccion_y_no_a_info(string configurado, string esperado)
    {
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync(destinatarioConfigurado: configurado);

        using var respuesta = await servidor.PostAsync(CuerpoBueno());

        respuesta.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var enviado = servidor.Correo.Enviados.Should().ContainSingle().Subject;
        enviado.Destinatario.Should().Be(esperado);
        enviado.ResponderA.Should().Be("ana@empresa.es", "Reply-To sigue siendo quien escribe, no el destinatario");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-es-un-correo")]
    [InlineData("Nombre Visible <demo@example.com>")]
    [InlineData("uno@example.com,otro@example.com")]
    [InlineData("demo@example")]
    [InlineData("démo@example.com")]
    [InlineData("demo@example.com\r\nBcc: otro@example.com")]
    public async Task Con_el_ajuste_vacio_o_invalido_el_correo_va_a_info_y_nunca_a_la_direccion_rota(string configurado)
    {
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync(destinatarioConfigurado: configurado);

        using var respuesta = await servidor.PostAsync(CuerpoBueno());

        respuesta.StatusCode.Should().Be(HttpStatusCode.Accepted);
        servidor.Correo.Enviados.Should().ContainSingle().Which.Destinatario.Should().Be("info@talveg.es");
    }

    [Fact]
    public void Un_ajuste_invalido_se_avisa_sin_escribir_el_valor_en_el_registro()
    {
        var registro = new RegistroCapturado();

        var destinatario = DestinatarioSolicitudDemo.Resolver("secreto-mal-escrito", registro);

        destinatario.Direccion.Should().Be("info@talveg.es");
        var aviso = registro.Entradas.Should().ContainSingle().Subject;
        aviso.Nivel.Should().Be(LogLevel.Warning);
        aviso.Texto.Should().Contain("Contacto:DestinatarioSolicitudDemo").And.NotContain("secreto-mal-escrito");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("demo@example.com")]
    public void Un_ajuste_ausente_vacio_o_valido_no_genera_aviso(string? configurado)
    {
        var registro = new RegistroCapturado();

        DestinatarioSolicitudDemo.Resolver(configurado, registro);

        registro.Entradas.Should().BeEmpty();
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

        using var respuesta = await servidor.PostAsync(CuerpoBueno(campoAdicional: "https://spam.example"));

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

        respuesta.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "no es la IP la que se pasa: un 429 le diría a quien escribe que espere, y no le toca a él");
        (await respuesta.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"cupo-agotado\"}");
        respuesta.Headers.RetryAfter!.Delta.Should().BePositive();
        respuesta.Headers.CacheControl!.NoStore.Should().BeTrue();
        servidor.Correo.Enviados.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("Email.EnvioCerrado")]
    [InlineData("Email.NoConfigurado")]
    [InlineData("Email.ErrorApi")]
    public async Task Un_fallo_que_demuestra_que_no_salio_nada_devuelve_su_plaza(string codigo)
    {
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync(limiteGlobalPorHora: 1);
        servidor.Correo.Respuesta = Result.Fallo(Error.Crear(codigo, "no salió"));
        (await servidor.PostAsync(CuerpoBueno())).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        servidor.Correo.Respuesta = Result.Exito();
        using var respuesta = await servidor.PostAsync(CuerpoBueno());

        respuesta.StatusCode.Should().Be(HttpStatusCode.Accepted,
            "con la guarda cerrada o el servidor rechazando la orden no salió nada");
        servidor.Correo.Enviados.Should().HaveCount(1);
    }

    [Fact]
    public void Los_codigos_que_devuelven_la_plaza_son_los_que_emiten_la_guarda_y_el_smtp()
    {
        // Si alguien renombra un código en Infrastructure, la plaza dejaría de volver sin que nada
        // se pusiera en rojo: el conjunto se compara con las constantes del origen.
        SolicitudDemoEndpoints.FallosSinEnvio.Should().BeEquivalentTo(
        [
            EnvioCorreoOptions.CodigoErrorEnvioCerrado,
            SmtpEmailService.CodigoNoConfigurado,
            SmtpEmailService.CodigoErrorApi,
        ]);
    }

    [Fact]
    public async Task Con_la_guarda_real_cerrada_la_plaza_vuelve()
    {
        // El código que devuelve la guarda de verdad, no uno escrito a mano en el test.
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync(limiteGlobalPorHora: 1, guardaCerrada: true);

        (await servidor.PostAsync(CuerpoBueno())).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var respuesta = await servidor.PostAsync(CuerpoBueno());

        (await respuesta.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"no-disponible\"}",
            "un 503 cupo-agotado aquí querría decir que la guarda cerrada gasta cupo");
        EnvioCorreoOptions.CodigoErrorEnvioCerrado.Should().BeOneOf(SolicitudDemoEndpoints.FallosSinEnvio);
    }

    [Fact]
    public async Task Un_resultado_incierto_del_smtp_conserva_la_plaza()
    {
        // Un vencimiento o un corte durante SendAsync puede llegar con el mensaje ya aceptado:
        // devolver la plaza dejaría saltarse el techo de correo cortando conexiones.
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync(limiteGlobalPorHora: 1);
        servidor.Correo.Respuesta = Result.Fallo(Error.Crear("Email.ErrorRed", "sin respuesta"));
        (await servidor.PostAsync(CuerpoBueno())).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        servidor.Correo.Respuesta = Result.Exito();
        using var respuesta = await servidor.PostAsync(CuerpoBueno());

        (await respuesta.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"cupo-agotado\"}");
    }

    [Fact]
    public async Task Una_excepcion_del_envio_es_503_en_json_sin_detalle_y_conserva_la_plaza()
    {
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync(limiteGlobalPorHora: 1);
        servidor.Correo.Excepcion = new InvalidOperationException("smtp interno: ana@empresa.es rechazada");

        using var respuesta = await servidor.PostAsync(CuerpoBueno());

        respuesta.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await respuesta.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"no-disponible\"}",
            "ASVS 4.0.3 V7.4.1: ni la página HTML de error de la aplicación ni el mensaje de la excepción");

        servidor.Correo.Excepcion = null;
        using var siguiente = await servidor.PostAsync(CuerpoBueno());
        (await siguiente.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"cupo-agotado\"}",
            "una excepción a mitad de SendAsync no demuestra que el mensaje no saliera");
    }

    [Fact]
    public async Task Las_peticiones_de_un_origen_ajeno_no_gastan_la_cuota_de_la_ip()
    {
        // Una página cualquiera puede hacer fetch() al endpoint desde el navegador de quien la
        // visita: si eso contara, le agotaría a esa persona el formulario de verdad.
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync(limitePorIp: 1);

        for (var i = 0; i < 3; i++)
            (await servidor.PostAsync(CuerpoBueno(), origen: "https://evil.example")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var respuesta = await servidor.PostAsync(CuerpoBueno());

        respuesta.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Un_cuerpo_chunked_sin_longitud_se_corta_en_el_maximo_y_no_antes()
    {
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync();
        var justo = JsonSerializer.Serialize(CuerpoBueno());
        justo += new string(' ', SolicitudDemoEndpoints.MaximoBytes - Encoding.UTF8.GetByteCount(justo));

        using (var enElLimite = await servidor.PostSinLongitudAsync(Encoding.UTF8.GetBytes(justo)))
            enElLimite.StatusCode.Should().Be(HttpStatusCode.Accepted, "8192 bytes exactos caben");

        using var pasado = await servidor.PostSinLongitudAsync(Encoding.UTF8.GetBytes(justo + " "));

        pasado.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge,
            "sin Content-Length el tope lo pone la lectura, no lo que declare el remitente");
        (await pasado.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"demasiado-grande\"}");
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
    public async Task Un_cuerpo_chunked_por_encima_del_tope_de_kestrel_es_413_en_json()
    {
        // Lo corta Kestrel con BadHttpRequestException 413 en vez del bucle: la rama del catch.
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync();

        using var respuesta = await servidor.PostSinLongitudAsync(new byte[SolicitudDemoEndpoints.TopeKestrel * 4]);

        respuesta.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await respuesta.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"demasiado-grande\"}");
    }

    [Fact]
    public async Task Si_quien_llama_cancela_durante_el_envio_la_cancelacion_se_propaga_y_la_plaza_no_vuelve()
    {
        var cupo = new CupoGlobalSolicitudDemo(1, TimeProvider.System);
        var correo = new CorreoFalso { Excepcion = new OperationCanceledException() };
        var contexto = new DefaultHttpContext();
        contexto.Request.Method = HttpMethods.Post;
        contexto.Request.Headers.Origin = OrigenTalveg;
        contexto.Request.ContentType = "application/json";
        contexto.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(CuerpoBueno()));

        var recibir = () => SolicitudDemoEndpoints.RecibirAsync(
            contexto, correo, cupo, DestinatarioSolicitudDemo.Resolver(null, NullLogger.Instance),
            NullLogger<SolicitudDemoPeticion>.Instance, CancellationToken.None);

        await recibir.Should().ThrowAsync<OperationCanceledException>();
        cupo.IntentarReservar(out _, out _, out _).Should().BeFalse(
            "el servidor SMTP pudo aceptar el mensaje antes de que se cortara la conexión");
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Otro_metodo_es_405_en_json_y_no_la_pagina_html_de_estado(string metodo)
    {
        await using var servidor = await ServidorSolicitudDemo.ArrancarAsync();

        using var respuesta = await servidor.Cliente.SendAsync(new HttpRequestMessage(new HttpMethod(metodo), SolicitudDemoEndpoints.Ruta));

        respuesta.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        (await respuesta.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"metodo-no-permitido\"}");
        respuesta.Content.Headers.Allow.Should().Equal("POST");
        respuesta.Headers.CacheControl!.NoStore.Should().BeTrue();
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

    [Fact]
    public void Dos_direcciones_del_mismo_64_comparten_cuota_y_un_origen_ajeno_no_cuenta()
    {
        // La partición que usa el limitador de verdad, no ClaveIp suelta: lee la IP de la conexión
        // (ya resuelta por UseForwardedHeaders) y el Origin.
        static HttpContext Peticion(string ip, string origen)
        {
            var contexto = new DefaultHttpContext();
            contexto.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            contexto.Request.Headers.Origin = origen;
            return contexto;
        }

        SolicitudDemoEndpoints.ClaveDeLimite(Peticion("2001:db8:1:2::1", OrigenTalveg))
            .Should().Be(SolicitudDemoEndpoints.ClaveDeLimite(Peticion("2001:db8:1:2:ffff::9", "https://www.talveg.es")))
            .And.Be("2001:db8:1:2::/64");
        SolicitudDemoEndpoints.ClaveDeLimite(Peticion("2001:db8:1:3::1", OrigenTalveg)).Should().Be("2001:db8:1:3::/64");
        SolicitudDemoEndpoints.ClaveDeLimite(Peticion("2001:db8:1:2::1", "https://evil.example")).Should().BeNull();
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
            int limitePorIp = 50, int limiteGlobalPorHora = 50, bool guardaCerrada = false,
            string? destinatarioConfigurado = null)
        {
            var correo = new CorreoFalso();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseKestrel(opciones => opciones.Listen(IPAddress.Loopback, 0));
            builder.Logging.ClearProviders();
            var ajustes = new Dictionary<string, string?>
            {
                ["RateLimiting:SolicitudDemo:LimitePorIp"] = limitePorIp.ToString(),
                ["RateLimiting:SolicitudDemo:LimiteGlobalPorHora"] = limiteGlobalPorHora.ToString(),
            };
            if (destinatarioConfigurado is not null)
                ajustes[DestinatarioSolicitudDemo.ClaveConfiguracion] = destinatarioConfigurado;
            builder.Configuration.AddInMemoryCollection(ajustes);
            builder.Services.AddSolicitudDemo(builder.Configuration);
            builder.Services.AddSingleton<IEmailService>(guardaCerrada
                ? new GuardaEnvioEmailService(correo, Options.Create(new EnvioCorreoOptions()), NullLogger<GuardaEnvioEmailService>.Instance)
                : correo);

            var app = builder.Build();
            app.UseExceptionHandler("/error");
            app.UseStatusCodePagesWithReExecute("/not-found");
            app.UseCors();
            app.UseRateLimiter();
            app.MapGet("/not-found", () => Results.Content("<html>pagina de estado</html>", "text/html"));
            app.Map("/error", () => Results.Content("<html>pagina de error</html>", "text/html"));
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

        /// <summary>Sin <c>Content-Length</c>: el contenido no sabe calcular su longitud y HttpClient lo manda chunked.</summary>
        public Task<HttpResponseMessage> PostSinLongitudAsync(byte[] cuerpo)
        {
            var peticion = new HttpRequestMessage(HttpMethod.Post, SolicitudDemoEndpoints.Ruta)
            {
                Content = new ContenidoSinLongitud(cuerpo),
            };
            peticion.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            peticion.Headers.Add("Origin", OrigenTalveg);
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

        public Exception? Excepcion { get; set; }

        public Task<Result> EnviarAsync(
            string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo,
            string? responderA = null, CancellationToken cancellationToken = default)
        {
            if (Excepcion is not null)
                throw Excepcion;

            if (Respuesta.EsExitoso)
                Enviados.Add((destinatarioEmail, asunto, cuerpoHtml, responderA));
            return Task.FromResult(Respuesta);
        }
    }

    private sealed class RegistroCapturado : ILogger
    {
        public List<(LogLevel Nivel, string Texto)> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Add((logLevel, formatter(state, exception)));
    }

    private sealed class ContenidoSinLongitud(byte[] cuerpo) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(cuerpo).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
