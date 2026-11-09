using System.Net;
using System.Text;
using CaeManager.Application.Integraciones;
using CaeManager.Infrastructure.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.Integraciones;

/// <summary>
/// Un buzón compartido se lee y se envía por <c>/users/{dirección}</c>; el buzón propio sigue en <c>/me</c>. Sin red: un manejador
/// anota cada petición y responde con lo mínimo que espera el cliente.
/// </summary>
public class Microsoft365BuzonCompartidoRutasTests
{
    private const string Raiz = "https://graph.microsoft.com/v1.0/";
    private static readonly BuzonGraph Compartido = new("piloto+cae@talveg.example", true);
    private static readonly BuzonGraph Propio = new("gestora@talveg.example", false);
    private static readonly string RaizCompartido = $"{Raiz}users/{Uri.EscapeDataString(Compartido.Direccion)}";

    private sealed class AnotaPeticionesHandler(HttpStatusCode estado = HttpStatusCode.OK, string cuerpo = "{}") : HttpMessageHandler
    {
        public List<(HttpMethod Metodo, string Url, string? Prefer)> Peticiones { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Peticiones.Add((request.Method, request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("Prefer", out var prefer) ? string.Join(",", prefer) : null));
            return Task.FromResult(new HttpResponseMessage(estado)
            {
                Content = new StringContent(cuerpo, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static Microsoft365GraphClient Cliente(HttpMessageHandler handler) =>
        new(new HttpClient(handler), Options.Create(new Microsoft365GraphOptions()), NullLogger<Microsoft365GraphClient>.Instance);

    [Fact]
    public void La_ruta_de_un_buzon_compartido_escapa_la_direccion_y_la_del_propio_es_me()
    {
        Microsoft365GraphClient.RutaBuzon(Compartido).Should().Be($"{Raiz}users/piloto%2Bcae%40talveg.example");
        Microsoft365GraphClient.RutaBuzon(Propio).Should().Be($"{Raiz}me");
    }

    [Fact]
    public async Task Los_mensajes_de_una_carpeta_compartida_se_leen_por_users_y_no_por_me()
    {
        var handler = new AnotaPeticionesHandler(cuerpo: """{"value":[]}""");

        var resultado = await Cliente(handler).ListarMensajesAsync("token", Compartido, "carpeta-1", 25, 0, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        handler.Peticiones.Should().ContainSingle()
            .Which.Url.Should().StartWith($"{RaizCompartido}/mailFolders/carpeta-1/messages");
    }

    [Fact]
    public async Task Un_mensaje_de_un_buzon_compartido_se_pide_por_users()
    {
        var handler = new AnotaPeticionesHandler(HttpStatusCode.NotFound);

        await Cliente(handler).ObtenerMensajeAsync("token", Compartido, "mensaje-1", CancellationToken.None);

        handler.Peticiones.Should().ContainSingle()
            .Which.Url.Should().StartWith($"{RaizCompartido}/messages/mensaje-1");
    }

    [Fact]
    public async Task Las_carpetas_de_un_buzon_compartido_se_listan_por_users()
    {
        var handler = new AnotaPeticionesHandler(cuerpo: """{"value":[]}""");

        var resultado = await Cliente(handler).ListarCarpetasAsync("token", Compartido, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        handler.Peticiones.Should().NotBeEmpty()
            .And.OnlyContain(p => p.Url.StartsWith($"{RaizCompartido}/mailFolders", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_mensaje_nuevo_se_redacta_y_se_envia_desde_el_buzon_de_la_conexion(bool compartido)
    {
        var buzon = compartido ? Compartido : Propio;
        var raiz = compartido ? RaizCompartido : $"{Raiz}me";
        var handler = new AnotaPeticionesHandler(HttpStatusCode.Created, """{"id":"m1","conversationId":"c1"}""");

        var resultado = await Cliente(handler).EnviarNuevoMensajeAsync(
            "token", buzon, ["contacto@empresa.example"], "Asunto", "<p>Hola</p>", null, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        handler.Peticiones.Select(p => p.Url).Should().Equal($"{raiz}/messages", $"{raiz}/messages/m1/send");
    }

    [Fact]
    public async Task La_respuesta_desde_un_buzon_compartido_va_por_users()
    {
        var handler = new AnotaPeticionesHandler(HttpStatusCode.Accepted);

        var resultado = await Cliente(handler).EnviarRespuestaAsync("token", Compartido, "origen-1", "<p>Hola</p>", null, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        handler.Peticiones.Should().ContainSingle().Which.Url.Should().Be($"{RaizCompartido}/messages/origen-1/reply");
    }

    [Theory]
    [InlineData(true, "users/piloto%2Bcae%40talveg.example/mailFolders('Inbox')/messages")]
    [InlineData(false, "me/mailFolders('Inbox')/messages")]
    public async Task La_suscripcion_apunta_a_la_bandeja_del_buzon_de_la_conexion(bool compartido, string recursoEsperado)
    {
        var handler = new CapturaCuerpoHandler(
            """{"id":"sub-1","expirationDateTime":"2026-11-11T00:00:00Z"}""");

        var resultado = await Cliente(handler).CrearSuscripcionAsync(
            "token", compartido ? Compartido : Propio, "https://app.example/webhook", "estado", CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        System.Text.Json.JsonDocument.Parse(handler.Cuerpo!).RootElement.GetProperty("resource").GetString()
            .Should().Be(recursoEsperado);
    }

    [Theory]
    [InlineData("no-es-un-guid")]
    [InlineData("common")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task El_token_de_aplicacion_solo_se_pide_a_un_directorio_con_GUID(string directorio)
    {
        var handler = new AnotaPeticionesHandler();

        var resultado = await Cliente(handler).ObtenerTokenAplicacionAsync(directorio, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.DirectorioInvalido");
        handler.Peticiones.Should().BeEmpty();
    }

    /// <summary>Token con forma de JWT (sin firma real) y el claim <c>roles</c> dado; sin roles, ningún permiso de Entra.</summary>
    private static string TokenJwt(params string[] roles)
    {
        static string Base64Url(string texto) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(texto)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var carga = roles.Length == 0 ? "{\"aud\":\"graph\"}" : $"{{\"roles\":[{string.Join(',', roles.Select(r => $"\"{r}\""))}]}}";
        return $"{Base64Url("{\"alg\":\"none\"}")}.{Base64Url(carga)}.firma";
    }

    private static string RespuestaToken(string token) => $"{{\"access_token\":\"{token}\",\"expires_in\":3599}}";

    private static Microsoft365GraphClient ClienteConSecreto(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        Options.Create(new Microsoft365GraphOptions { ClientId = "cliente", ClientSecret = "secreto" }),
        NullLogger<Microsoft365GraphClient>.Instance);

    /// <summary>
    /// Un permiso de aplicación de correo concedido en Entra abre todos los buzones del directorio y deja sin efecto
    /// el ámbito de RBAC for Applications de Exchange (los dos se suman): con él, el token no se usa. Tampoco con
    /// cualquier otro permiso de aplicación de Entra: el token solo vale si no trae ninguno.
    /// </summary>
    [Theory]
    [InlineData("Mail.Read")]
    [InlineData("Mail.ReadWrite")]
    [InlineData("mail.send")]
    [InlineData("User.Read.All")]
    public async Task Un_token_de_aplicacion_con_permiso_de_correo_sin_acotar_no_se_usa(string rol)
    {
        var directorio = Guid.NewGuid().ToString();
        var handler = new AnotaPeticionesHandler(cuerpo: RespuestaToken(TokenJwt(rol)));
        var cliente = ClienteConSecreto(handler);

        var resultado = await cliente.ObtenerTokenAplicacionAsync(directorio, CancellationToken.None);
        var segundo = await cliente.ObtenerTokenAplicacionAsync(directorio, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.PermisoSinAcotar");
        segundo.EsFallido.Should().BeTrue("un token rechazado no se guarda en la caché");
        handler.Peticiones.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("opaco")]
    [InlineData("a.no-es-base64!.c")]
    public async Task Un_token_de_aplicacion_ilegible_no_se_usa(string token)
    {
        var handler = new AnotaPeticionesHandler(cuerpo: RespuestaToken(token));

        var resultado = await ClienteConSecreto(handler).ObtenerTokenAplicacionAsync(Guid.NewGuid().ToString(), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.PermisoSinAcotar");
    }

    [Fact]
    public async Task Un_token_de_aplicacion_sin_permisos_de_Entra_se_acepta()
    {
        var token = TokenJwt();
        var handler = new AnotaPeticionesHandler(cuerpo: RespuestaToken(token));

        var resultado = await ClienteConSecreto(handler).ObtenerTokenAplicacionAsync(Guid.NewGuid().ToString(), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.AccessToken.Should().Be(token);
    }

    [Fact]
    public async Task El_token_de_aplicacion_se_pide_al_endpoint_del_directorio_con_client_credentials()
    {
        var directorio = Guid.NewGuid();
        var token = TokenJwt();
        var handler = new CapturaCuerpoHandler(RespuestaToken(token));
        var cliente = new Microsoft365GraphClient(
            new HttpClient(handler),
            Options.Create(new Microsoft365GraphOptions { ClientId = "cliente", ClientSecret = "secreto" }),
            NullLogger<Microsoft365GraphClient>.Instance);

        var resultado = await cliente.ObtenerTokenAplicacionAsync(directorio.ToString(), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.AccessToken.Should().Be(token);
        handler.Url.Should().Be($"https://login.microsoftonline.com/{directorio:D}/oauth2/v2.0/token");
        handler.Cuerpo.Should().Contain("grant_type=client_credentials")
            .And.Contain("scope=https%3A%2F%2Fgraph.microsoft.com%2F.default")
            .And.NotContain("refresh_token");
    }

    [Fact]
    public async Task El_token_de_aplicacion_de_un_directorio_se_reutiliza_mientras_esta_vigente()
    {
        var directorio = Guid.NewGuid().ToString();
        var token = TokenJwt();
        var handler = new AnotaPeticionesHandler(cuerpo: RespuestaToken(token));
        var cliente = ClienteConSecreto(handler);

        (await cliente.ObtenerTokenAplicacionAsync(directorio, CancellationToken.None)).Valor.AccessToken.Should().Be(token);
        (await cliente.ObtenerTokenAplicacionAsync(directorio, CancellationToken.None)).Valor.AccessToken.Should().Be(token);

        handler.Peticiones.Should().ContainSingle();
    }

    [Fact]
    public async Task El_directorio_se_lee_de_organization_con_el_token_delegado()
    {
        var handler = new AnotaPeticionesHandler(cuerpo: """{"value":[{"id":"AABBCCDD-0000-1111-2222-333344445555"}]}""");

        var resultado = await Cliente(handler).ObtenerDirectorioAsync("token", CancellationToken.None);

        resultado.Valor.Should().Be("aabbccdd-0000-1111-2222-333344445555");
        handler.Peticiones.Should().ContainSingle().Which.Url.Should().StartWith($"{Raiz}organization");
    }

    [Theory]
    [InlineData("""{"value":[]}""")]
    [InlineData("""{"value":[{"id":"aabbccdd-0000-1111-2222-333344445555"},{"id":"11111111-0000-1111-2222-333344445555"}]}""")]
    [InlineData("""{"value":[{"id":"no-es-un-guid"}]}""")]
    [InlineData("""{"value":[{"id":"00000000-0000-0000-0000-000000000000"}]}""")]
    [InlineData("no es json")]
    public async Task Un_directorio_ausente_ambiguo_o_mal_formado_no_se_adivina(string cuerpo)
    {
        var resultado = await Cliente(new AnotaPeticionesHandler(cuerpo: cuerpo)).ObtenerDirectorioAsync("token", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.DirectorioInvalido");
    }

    private sealed class CapturaCuerpoHandler(string respuesta) : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string? Cuerpo { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.AbsoluteUri;
            Cuerpo = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(respuesta, Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task La_identidad_es_el_Id_inmutable_de_la_bandeja_de_entrada_del_buzon_compartido()
    {
        var handler = new AnotaPeticionesHandler(cuerpo: """{"id":"AAMkInboxInmutable="}""");

        var resultado = await Cliente(handler).ObtenerIdentidadBuzonAsync("token", Compartido, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be("AAMkInboxInmutable=");
        var peticion = handler.Peticiones.Should().ContainSingle().Which;
        peticion.Url.Should().StartWith($"{RaizCompartido}/mailFolders('Inbox')");
        peticion.Prefer.Should().Be("IdType=\"ImmutableId\"");
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"id":""}""")]
    [InlineData("""no es json""")]
    public async Task Una_bandeja_sin_Id_no_da_identidad(string cuerpo)
    {
        var resultado = await Cliente(new AnotaPeticionesHandler(cuerpo: cuerpo))
            .ObtenerIdentidadBuzonAsync("token", Compartido, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.ErrorApi");
    }

    [Fact]
    public async Task Un_timeout_de_Graph_en_la_identidad_es_un_fallo_recuperable_y_no_una_cancelacion()
    {
        var resultado = await Cliente(new LanzaTimeoutHandler())
            .ObtenerIdentidadBuzonAsync("token", Compartido, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.TiempoAgotado");
    }

    [Fact]
    public async Task Un_timeout_de_Entra_al_pedir_el_token_de_aplicacion_es_un_fallo_recuperable()
    {
        var resultado = await Cliente(new LanzaTimeoutHandler())
            .ObtenerTokenAplicacionAsync(Guid.NewGuid().ToString(), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.TiempoAgotado");
    }

    [Fact]
    public async Task La_cancelacion_pedida_de_verdad_se_propaga()
    {
        using var cancelacion = new CancellationTokenSource();
        await cancelacion.CancelAsync();

        var accion = () => Cliente(new LanzaTimeoutHandler())
            .ObtenerTokenAplicacionAsync(Guid.NewGuid().ToString(), cancelacion.Token);

        await accion.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "Integraciones.Microsoft365.MensajeNoEncontrado")]
    [InlineData(HttpStatusCode.Forbidden, "Integraciones.Microsoft365.ErrorApi")]
    [InlineData(HttpStatusCode.TooManyRequests, "Integraciones.Microsoft365.ErrorApi")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Integraciones.Microsoft365.ErrorApi")]
    public async Task Leer_un_mensaje_solo_da_por_borrado_el_404(HttpStatusCode estado, string codigo)
    {
        var resultado = await Cliente(new AnotaPeticionesHandler(estado))
            .ObtenerMensajeAsync("token", Compartido, "msg-1", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(codigo);
    }

    [Theory]
    [InlineData(true, HttpStatusCode.Forbidden, "Integraciones.Microsoft365.SinPermisoEscritura")]
    [InlineData(true, HttpStatusCode.InternalServerError, "Integraciones.Microsoft365.ErrorEnvio")]
    [InlineData(false, HttpStatusCode.Forbidden, "Integraciones.Microsoft365.ErrorEnvio")]
    public async Task Sin_permiso_de_escritura_el_compartido_lo_dice_al_crear_el_borrador(bool compartido, HttpStatusCode estado, string codigo)
    {
        var buzon = compartido ? Compartido : new BuzonGraph("propio@tenant.com", false);

        var resultado = await Cliente(new AnotaPeticionesHandler(estado))
            .EnviarNuevoMensajeAsync("token", buzon, ["destino@otra.es"], "Asunto", "<p>Hola</p>", null, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(codigo);
    }

    private sealed class LanzaTimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Lo que hace HttpClient al vencer su Timeout: cancela sin que el llamador lo haya pedido.
            cancellationToken.ThrowIfCancellationRequested();
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "Integraciones.Microsoft365.SinAccesoBuzon")]
    [InlineData(HttpStatusCode.NotFound, "Integraciones.Microsoft365.SinAccesoBuzon")]
    [InlineData(HttpStatusCode.InternalServerError, "Integraciones.Microsoft365.ErrorApi")]
    public async Task Comprobar_acceso_distingue_la_falta_de_permiso_de_un_fallo_de_Graph(HttpStatusCode estado, string codigo)
    {
        var resultado = await Cliente(new AnotaPeticionesHandler(estado))
            .ObtenerIdentidadBuzonAsync("token", Compartido, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(codigo);
    }
}
