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

    private static readonly Func<CancellationToken, Task<bool>> PuertaAbierta = _ => Task.FromResult(true);

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

        var resultado = await Cliente(handler).ListarCarpetasAsync("token", Compartido, _ => Task.FromResult(true), CancellationToken.None);

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
            "token", buzon, ["contacto@empresa.example"], "Asunto", "<p>Hola</p>", null, PuertaAbierta, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        handler.Peticiones.Select(p => p.Url).Should().Equal($"{raiz}/messages", $"{raiz}/messages/m1/send");
    }

    /// <summary>
    /// La conexión deja de estar operativa (o no se puede comprobar) mientras Graph crea el borrador: la puerta, que se
    /// evalúa entre crear y enviar, lo impide. El borrador se borra y nunca llega a /send.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Si_la_puerta_no_pasa_entre_crear_y_enviar_el_borrador_se_borra_y_no_se_envia(bool puertaLanza)
    {
        var handler = new AnotaPeticionesHandler(HttpStatusCode.Created, """{"id":"m1","conversationId":"c1"}""");
        var evaluaciones = 0;
        Func<CancellationToken, Task<bool>> puerta = _ =>
        {
            evaluaciones++;
            return puertaLanza ? throw new InvalidOperationException("la base no responde") : Task.FromResult(false);
        };

        var resultado = await Cliente(handler).EnviarNuevoMensajeAsync(
            "token", Compartido, ["contacto@empresa.example"], "Asunto", "<p>Hola</p>", null, puerta, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ErroresBuzonSinVerificar.Codigo);
        evaluaciones.Should().Be(1);
        handler.Peticiones.Select(p => (p.Metodo, p.Url)).Should().Equal(
            (HttpMethod.Post, $"{RaizCompartido}/messages"),
            (HttpMethod.Delete, $"{RaizCompartido}/messages/m1"));
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

    [Theory]
    [InlineData("Integraciones.Microsoft365.BuzonYaConectado", "/integraciones?error=ya-conectado")]
    [InlineData("Integraciones.Microsoft365.BuzonYaConectadoEnEstaOrganizacion", "/integraciones?error=ya-conectado")]
    [InlineData("Integraciones.Microsoft365.ErrorSuscripcion", "/integraciones?error=suscripcion")]
    [InlineData("Integraciones.Microsoft365.AltaCompartidaEnPausa", "/integraciones?error=compartidos-en-pausa")]
    public void Un_buzon_ya_conectado_vuelve_con_su_propio_aviso_y_no_con_el_de_suscripcion(string codigo, string esperada) =>
        CaeManager.Web.Features.Integraciones.Endpoints.ConectarMicrosoft365Endpoints
            .RedireccionTrasConectar(CaeManager.Domain.Common.Result.Fallo<Guid>(CaeManager.Domain.Common.Error.Crear(codigo, "x")))
            .Should().Be(esperada);

    /// <summary>Token con forma de JWT y la carga dada tal cual.</summary>
    private static string TokenJwtConCarga(string carga)
    {
        static string Base64Url(string texto) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(texto)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{Base64Url("{\"alg\":\"none\"}")}.{Base64Url(carga)}.firma";
    }

    [Theory]
    [InlineData("""{"roles":"Mail.Read"}""")]
    [InlineData("""{"roles":{"0":"Mail.Read"}}""")]
    public async Task Un_token_de_aplicacion_con_roles_que_no_son_una_lista_no_se_usa(string carga)
    {
        var handler = new AnotaPeticionesHandler(cuerpo: RespuestaToken(TokenJwtConCarga(carga)));

        var resultado = await ClienteConSecreto(handler).ObtenerTokenAplicacionAsync(Guid.NewGuid().ToString(), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.PermisoSinAcotar");
    }

    [Fact]
    public async Task Un_token_de_aplicacion_con_la_lista_de_roles_vacia_se_acepta()
    {
        var token = TokenJwtConCarga("""{"roles":[]}""");
        var handler = new AnotaPeticionesHandler(cuerpo: RespuestaToken(token));

        var resultado = await ClienteConSecreto(handler).ObtenerTokenAplicacionAsync(Guid.NewGuid().ToString(), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.AccessToken.Should().Be(token);
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

    private static string RespuestaTokensDelegados(string accessToken) =>
        $"{{\"access_token\":\"{accessToken}\",\"refresh_token\":\"refresco\",\"expires_in\":3599}}";

    /// <summary>
    /// El directorio de quien inicia sesión es el claim «tid» del access token que devuelve el endpoint de tokens en
    /// el canje del código, normalizado. Un «tid» distinto en el código o en la query del callback no se mira.
    /// </summary>
    [Fact]
    public async Task El_directorio_es_el_tid_del_token_del_canje_y_no_lo_que_llegue_con_el_codigo()
    {
        var token = TokenJwtConCarga("""{"tid":"AABBCCDD-0000-1111-2222-333344445555"}""");
        var handler = new CapturaCuerpoHandler(RespuestaTokensDelegados(token));

        var resultado = await ClienteConSecreto(handler).IntercambiarCodigoPorTokensAsync(
            "codigo&tid=99999999-0000-1111-2222-333344445555", "https://app.test/callback?tid=99999999-0000-1111-2222-333344445555",
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.DirectorioEntraId.Should().Be("aabbccdd-0000-1111-2222-333344445555");
        handler.Url.Should().EndWith("/oauth2/v2.0/token");
    }

    [Theory]
    [InlineData("""{"aud":"graph"}""")]
    [InlineData("""{"tid":"no-es-un-guid"}""")]
    [InlineData("""{"tid":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("""{"tid":12345}""")]
    [InlineData("""{"tid":["aabbccdd-0000-1111-2222-333344445555"]}""")]
    public async Task Un_tid_ausente_o_mal_formado_deja_el_directorio_vacio_y_no_se_adivina(string carga)
    {
        var handler = new CapturaCuerpoHandler(RespuestaTokensDelegados(TokenJwtConCarga(carga)));

        var resultado = await ClienteConSecreto(handler).IntercambiarCodigoPorTokensAsync("codigo", "https://app.test/callback", CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue("el buzón propio no necesita el directorio; el alta del compartido sí lo exige");
        resultado.Valor.DirectorioEntraId.Should().BeNull();
    }

    [Fact]
    public async Task Un_access_token_que_no_es_un_JWT_deja_el_directorio_vacio()
    {
        var handler = new CapturaCuerpoHandler(RespuestaTokensDelegados("opaco"));

        var resultado = await ClienteConSecreto(handler).IntercambiarCodigoPorTokensAsync("codigo", "https://app.test/callback", CancellationToken.None);

        resultado.Valor.DirectorioEntraId.Should().BeNull();
    }

    [Fact]
    public void Una_conexion_correcta_vuelve_a_Conexiones_con_el_aviso_de_conectado() =>
        CaeManager.Web.Features.Integraciones.Endpoints.ConectarMicrosoft365Endpoints
            .RedireccionTrasConectar(CaeManager.Domain.Common.Result.Exito(Guid.NewGuid()))
            .Should().Be("/integraciones?conectado=true");

    private sealed class RespuestaFijaHandler(string cuerpo, string tipo) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(cuerpo, Encoding.UTF8, "application/json")
                {
                    Headers = { ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(tipo) },
                },
            });
    }

    /// <summary>Un 200 con un cuerpo ilegible (JSON roto o un charset que no existe) es un fallo, nunca una excepción.</summary>
    [Theory]
    [InlineData("no es json", "application/json")]
    [InlineData("{}", "application/json; charset=no-existe")]
    public async Task Un_200_ilegible_del_token_de_aplicacion_es_un_fallo(string cuerpo, string tipo)
    {
        var resultado = await ClienteConSecreto(new RespuestaFijaHandler(cuerpo, tipo))
            .ObtenerTokenAplicacionAsync(Guid.NewGuid().ToString(), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
    }

    [Theory]
    [InlineData("no es json", "application/json")]
    [InlineData("{}", "application/json; charset=no-existe")]
    public async Task Un_200_ilegible_de_un_mensaje_es_un_fallo(string cuerpo, string tipo)
    {
        var resultado = await Cliente(new RespuestaFijaHandler(cuerpo, tipo)).ObtenerMensajeAsync("token", Compartido, "mensaje-1", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
    }

    [Theory]
    [InlineData("no es json", "application/json")]
    [InlineData("{}", "application/json; charset=no-existe")]
    public async Task Un_200_ilegible_del_canje_de_tokens_es_un_fallo_transitorio(string cuerpo, string tipo)
    {
        var resultado = await ClienteConSecreto(new RespuestaFijaHandler(cuerpo, tipo))
            .IntercambiarCodigoPorTokensAsync("codigo", "https://app.test/callback", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(CodigosErrorMicrosoft365.Transitorio, "un cuerpo ilegible no es un rechazo de Entra");
    }

    /// <summary>Graph puede devolver nulos dentro de las listas: el mensaje se lee igual, sin esos elementos.</summary>
    [Fact]
    public async Task Un_mensaje_con_destinatarios_y_cabeceras_nulos_se_lee_sin_lanzar()
    {
        const string cuerpo = """{"id":"mensaje-1","conversationId":"hilo-1","subject":"Asunto","from":{"emailAddress":{"address":"remitente@tenant.com"}},"toRecipients":[null],"ccRecipients":[null,{"emailAddress":null}],"replyTo":[null],"internetMessageHeaders":[null],"body":{"contentType":"html","content":"<p>hola</p>"},"receivedDateTime":"2026-11-09T10:00:00Z","hasAttachments":false,"attachments":[]}""";

        var resultado = await Cliente(new RespuestaFijaHandler(cuerpo, "application/json")).ObtenerMensajeAsync("token", Compartido, "mensaje-1", CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.MensajeExternoId.Should().Be("mensaje-1");
    }

    /// <summary>
    /// Ronda 10: un 200 sin un campo obligatorio (CodigosErrorMicrosoft365.MensajeIncompleto) no se ingiere con datos
    /// inventados: remitente, fecha, hilo, cuerpo y lista de adjuntos (con id y nombre en cada uno).
    /// </summary>
    [Theory]
    [InlineData("\"from\":null,")]
    [InlineData("\"from\":{\"emailAddress\":{\"address\":\" \"}},")]
    [InlineData("\"receivedDateTime\":null,")]
    [InlineData("\"conversationId\":null,")]
    [InlineData("\"body\":null,")]
    [InlineData("\"attachments\":null,")]
    [InlineData("\"attachments\":[null],")]
    [InlineData("\"attachments\":[{\"id\":\"a-1\",\"name\":null}],")]
    public async Task Un_200_sin_un_campo_obligatorio_es_un_mensaje_incompleto(string sustitucion)
    {
        var campo = sustitucion[..(sustitucion.IndexOf(':') + 1)];
        var completo = new Dictionary<string, string>
        {
            ["\"from\":"] = "\"from\":{\"emailAddress\":{\"address\":\"remitente@tenant.com\"}},",
            ["\"receivedDateTime\":"] = "\"receivedDateTime\":\"2026-11-09T10:00:00Z\",",
            ["\"conversationId\":"] = "\"conversationId\":\"hilo-1\",",
            ["\"body\":"] = "\"body\":{\"contentType\":\"html\",\"content\":\"\"},",
            ["\"attachments\":"] = "\"attachments\":[],",
        };
        completo[campo] = sustitucion;
        var cuerpo = "{" + string.Concat(completo.Values) + "\"subject\":\"Asunto\"}";

        var resultado = await Cliente(new RespuestaFijaHandler(cuerpo, "application/json")).ObtenerMensajeAsync("token", Compartido, "mensaje-1", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(CodigosErrorMicrosoft365.MensajeIncompleto);
    }

    [Fact]
    public async Task Un_200_completo_con_cuerpo_vacio_y_sin_adjuntos_se_lee()
    {
        const string cuerpo = """{"from":{"emailAddress":{"address":"remitente@tenant.com"}},"receivedDateTime":"2026-11-09T10:00:00Z","conversationId":"hilo-1","body":{"contentType":"html","content":""},"attachments":[],"subject":"Asunto"}""";

        var resultado = await Cliente(new RespuestaFijaHandler(cuerpo, "application/json")).ObtenerMensajeAsync("token", Compartido, "mensaje-1", CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Remitente.Should().Be("remitente@tenant.com");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public async Task Crear_suscripcion_distingue_el_fallo_transitorio_del_definitivo(HttpStatusCode estado, bool transitorio)
    {
        var resultado = await Cliente(new AnotaPeticionesHandler(estado))
            .CrearSuscripcionAsync("token", Compartido, "https://app.operador.test/hook", "estado", CancellationToken.None);

        CodigosErrorMicrosoft365.EsTransitorio(resultado.Error).Should().Be(transitorio);
    }

    [Fact]
    public async Task Un_timeout_al_crear_la_suscripcion_es_transitorio()
    {
        var resultado = await Cliente(new LanzaTimeoutHandler())
            .CrearSuscripcionAsync("token", Compartido, "https://app.operador.test/hook", "estado", CancellationToken.None);

        CodigosErrorMicrosoft365.EsTransitorio(resultado.Error).Should().BeTrue();
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
    public async Task Un_timeout_de_Graph_al_eliminar_la_suscripcion_es_un_fallo_transitorio()
    {
        var resultado = await Cliente(new LanzaTimeoutHandler())
            .EliminarSuscripcionAsync("token", "sub-1", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        CodigosErrorMicrosoft365.EsTransitorio(resultado.Error).Should().BeTrue();
    }

    [Fact]
    public async Task La_cancelacion_pedida_al_eliminar_la_suscripcion_se_propaga()
    {
        using var cancelacion = new CancellationTokenSource();
        await cancelacion.CancelAsync();

        var accion = () => Cliente(new LanzaTimeoutHandler())
            .EliminarSuscripcionAsync("token", "sub-1", cancelacion.Token);

        await accion.Should().ThrowAsync<OperationCanceledException>();
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

    /// <summary>
    /// Solo un rechazo de Microsoft es definitivo. Un 408, 429 o 5xx al pedir un token (de aplicación o delegado) o al
    /// renovar una suscripción es transitorio: la renovación lo reintenta sin pasar la conexión a ConError.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task Pedir_token_y_renovar_distinguen_el_fallo_transitorio_del_definitivo(HttpStatusCode estado, bool transitorio)
    {
        var aplicacion = await ClienteConSecreto(new AnotaPeticionesHandler(estado))
            .ObtenerTokenAplicacionAsync(Guid.NewGuid().ToString(), CancellationToken.None);
        var delegado = await ClienteConSecreto(new AnotaPeticionesHandler(estado))
            .RefrescarTokensAsync("refresh", CancellationToken.None);
        var renovacion = await Cliente(new AnotaPeticionesHandler(estado))
            .RenovarSuscripcionAsync("token", "suscripcion-1", CancellationToken.None);

        foreach (var error in new[] { aplicacion.Error, delegado.Error, renovacion.Error })
            CodigosErrorMicrosoft365.EsTransitorio(error).Should().Be(transitorio, $"{(int)estado} con código {error.Codigo}");
        aplicacion.Error.Codigo.Should().Be(transitorio ? CodigosErrorMicrosoft365.Transitorio : "Integraciones.Microsoft365.ErrorAutenticacion");
        renovacion.Error.Codigo.Should().Be(transitorio ? CodigosErrorMicrosoft365.Transitorio : "Integraciones.Microsoft365.ErrorSuscripcion");
    }

    [Fact]
    public async Task Un_timeout_de_Graph_al_renovar_la_suscripcion_es_un_fallo_transitorio()
    {
        var resultado = await Cliente(new LanzaTimeoutHandler())
            .RenovarSuscripcionAsync("token", "suscripcion-1", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        CodigosErrorMicrosoft365.EsTransitorio(resultado.Error).Should().BeTrue();
    }

    /// <summary>Un 404 sin código de Graph («{}») no dice qué falta: puede ser el buzón y no se da por borrado (R).</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound, "Integraciones.Microsoft365.BuzonNoEncontrado")]
    [InlineData(HttpStatusCode.Forbidden, "Integraciones.Microsoft365.ErrorApi")]
    [InlineData(HttpStatusCode.TooManyRequests, "Integraciones.Microsoft365.ErrorApi")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Integraciones.Microsoft365.ErrorApi")]
    public async Task Leer_un_mensaje_no_da_por_borrado_un_404_sin_codigo_ni_ningun_otro_fallo(HttpStatusCode estado, string codigo)
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
            .EnviarNuevoMensajeAsync("token", buzon, ["destino@otra.es"], "Asunto", "<p>Hola</p>", null, PuertaAbierta, CancellationToken.None);

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

    /// <summary>Responde con lo que decida la función para cada petición; anota las peticiones como el otro manejador.</summary>
    private sealed class RespuestaPorPeticionHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<(HttpMethod Metodo, string Url)> Peticiones { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Peticiones.Add((request.Method, request.RequestUri!.AbsoluteUri));
            return Task.FromResult(responder(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode estado, string cuerpo) =>
        new(estado) { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") };

    /// <summary>
    /// O: un 200 del endpoint de tokens con JSON válido pero sin el token obligatorio no es un rechazo de Entra (que
    /// respondería 4xx): es transitorio, como una respuesta ilegible, y no lleva la conexión a ConError.
    /// </summary>
    [Theory]
    [InlineData("""{"token_type":"Bearer","expires_in":3599}""")]
    [InlineData("""{"access_token":"","expires_in":3599}""")]
    public async Task Un_200_sin_token_de_aplicacion_es_transitorio(string cuerpo)
    {
        var handler = new AnotaPeticionesHandler(cuerpo: cuerpo);

        var resultado = await ClienteConSecreto(handler).ObtenerTokenAplicacionAsync(Guid.NewGuid().ToString(), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        CodigosErrorMicrosoft365.EsTransitorio(resultado.Error).Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"access_token":"a","expires_in":3599}""")]
    [InlineData("""{"refresh_token":"r","expires_in":3599}""")]
    [InlineData("""{"expires_in":3599}""")]
    public async Task Un_200_sin_alguno_de_los_dos_tokens_al_refrescar_es_transitorio(string cuerpo)
    {
        var handler = new AnotaPeticionesHandler(cuerpo: cuerpo);

        var resultado = await ClienteConSecreto(handler).RefrescarTokensAsync("refresh", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        CodigosErrorMicrosoft365.EsTransitorio(resultado.Error).Should().BeTrue();
    }

    /// <summary>Un 429 con Retry-After lleva la espera pedida en el error, para que la verificación no vuelva antes.</summary>
    [Fact]
    public async Task Un_429_con_Retry_After_lleva_la_espera_en_el_error()
    {
        var handler = new RespuestaPorPeticionHandler(_ =>
        {
            var respuesta = Json((HttpStatusCode)429, """{"error":{"code":"TooManyRequests"}}""");
            respuesta.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return respuesta;
        });

        var identidad = await Cliente(handler).ObtenerIdentidadBuzonAsync("token", Compartido, CancellationToken.None);
        var token = await ClienteConSecreto(handler).ObtenerTokenAplicacionAsync(Guid.NewGuid().ToString(), CancellationToken.None);

        identidad.EsFallido.Should().BeTrue();
        identidad.Error.Codigo.Should().NotBe(CodigosErrorMicrosoft365.SinAccesoBuzon, "un 429 no es definitivo");
        identidad.Error.ReintentarTras.Should().Be(TimeSpan.FromSeconds(120));
        token.Error.ReintentarTras.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task Un_429_sin_Retry_After_no_inventa_una_espera()
    {
        var handler = new RespuestaPorPeticionHandler(_ => Json((HttpStatusCode)429, """{"error":{"code":"TooManyRequests"}}"""));

        var identidad = await Cliente(handler).ObtenerIdentidadBuzonAsync("token", Compartido, CancellationToken.None);

        identidad.EsFallido.Should().BeTrue();
        identidad.Error.ReintentarTras.Should().BeNull();
    }

    /// <summary>
    /// R: solo un 404 con <c>ErrorItemNotFound</c> es un mensaje que ya no está. Cualquier otro 404 (buzón inexistente,
    /// buzón sin REST, usuario inválido, recurso no encontrado o cuerpo ilegible) puede ser del buzón y no se da por
    /// resuelto.
    /// </summary>
    [Theory]
    [InlineData("""{"error":{"code":"ErrorItemNotFound","message":"The specified object was not found in the store."}}""", CodigosErrorMicrosoft365.MensajeNoEncontrado)]
    [InlineData("""{"error":{"code":"ResourceNotFound","message":"Resource could not be discovered."}}""", CodigosErrorMicrosoft365.BuzonNoEncontrado)]
    [InlineData("""{"error":{"code":"MailboxNotEnabledForRESTAPI"}}""", CodigosErrorMicrosoft365.BuzonNoEncontrado)]
    [InlineData("""{"error":{"code":"ErrorInvalidUser"}}""", CodigosErrorMicrosoft365.BuzonNoEncontrado)]
    [InlineData("no es json", CodigosErrorMicrosoft365.BuzonNoEncontrado)]
    [InlineData("", CodigosErrorMicrosoft365.BuzonNoEncontrado)]
    public async Task Un_404_al_leer_un_mensaje_solo_es_mensaje_eliminado_con_ErrorItemNotFound(string cuerpo, string codigoEsperado)
    {
        var handler = new RespuestaPorPeticionHandler(_ => Json(HttpStatusCode.NotFound, cuerpo));

        var resultado = await Cliente(handler).ObtenerMensajeAsync("token", Compartido, "mensaje-1", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(codigoEsperado);
    }

    /// <summary>S: cancelar durante el borrado del borrador que no pasó la puerta propaga la cancelación, no un fallo ordinario.</summary>
    [Fact]
    public async Task La_cancelacion_pedida_durante_el_borrado_del_borrador_se_propaga()
    {
        using var cancelacion = new CancellationTokenSource();
        var handler = new RespuestaPorPeticionHandler(peticion =>
        {
            if (peticion.Method == HttpMethod.Delete)
            {
                cancelacion.Cancel();
                throw new OperationCanceledException(cancelacion.Token);
            }

            return Json(HttpStatusCode.Created, """{"id":"m1","conversationId":"c1"}""");
        });

        await FluentActions.Awaiting(() => Cliente(handler).EnviarNuevoMensajeAsync(
                "token", Compartido, ["contacto@empresa.example"], "Asunto", "<p>Hola</p>", null,
                _ => Task.FromResult(false), cancelacion.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        handler.Peticiones.Select(p => p.Metodo).Should().Equal(HttpMethod.Post, HttpMethod.Delete);
    }

    /// <summary>Sin cancelación pedida, un fallo del borrado sigue siendo de mejor esfuerzo: el envío falla sin enviar.</summary>
    [Fact]
    public async Task Un_fallo_del_borrado_sin_cancelacion_pedida_no_se_propaga()
    {
        var handler = new RespuestaPorPeticionHandler(peticion => peticion.Method == HttpMethod.Delete
            ? throw new TaskCanceledException("tiempo agotado")
            : Json(HttpStatusCode.Created, """{"id":"m1","conversationId":"c1"}"""));

        var resultado = await Cliente(handler).EnviarNuevoMensajeAsync(
            "token", Compartido, ["contacto@empresa.example"], "Asunto", "<p>Hola</p>", null, _ => Task.FromResult(false), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ErroresBuzonSinVerificar.Codigo);
    }

    /// <summary>
    /// N: el listado pide las hijas de cada carpeta en peticiones aparte; antes de cada una vuelve a pasar la puerta, y si
    /// la conexión ya no está operativa no pide nada más.
    /// </summary>
    [Fact]
    public async Task El_listado_de_carpetas_no_pide_las_hijas_si_la_puerta_se_cierra()
    {
        var handler = new AnotaPeticionesHandler(cuerpo: """{"value":[{"id":"inbox","displayName":"Bandeja de entrada","childFolderCount":2}]}""");
        var puertas = 0;

        var resultado = await Cliente(handler).ListarCarpetasAsync(
            "token", Compartido, _ => Task.FromResult(++puertas < 1), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ErroresBuzonSinVerificar.Codigo);
        puertas.Should().Be(1);
        handler.Peticiones.Should().ContainSingle("solo la petición de las carpetas raíz, ninguna de las hijas");
    }
}