using System.Net;
using System.Reflection;
using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.ConectarBuzonMicrosoft365;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Integraciones;
using CaeManager.Web.Features.Integraciones.Endpoints;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.Integraciones;

/// <summary>
/// Conectar un buzón de Microsoft 365 nunca acaba en un JSON de problema ni en un aviso genérico que culpe a la configuración
/// cuando la causa es otra. Dos partes, sin red: el cliente real de Graph con un <see cref="HttpMessageHandler"/> falso (cómo se
/// clasifica cada respuesta de Microsoft: por estado HTTP y por código de error de Graph, nunca por el texto libre), y los dos
/// endpoints ejecutados de verdad (patrón de <c>CentroApiDetalleEndpointTests</c>) con ese mismo cliente y el servicio de alta
/// real. Toda vuelta a la aplicación es una redirección local a <c>/integraciones?error=</c> con un código de la lista cerrada.
/// </summary>
public class Microsoft365ConectarErroresTests
{
    private const string UrlPublica = "https://app.talveg.test";
    private const string RaizRedireccionError = "/integraciones?error=";

    /// <summary>Los códigos que el endpoint puede poner en «?error=»; Conexiones tiene un texto fijo para cada uno.</summary>
    private static readonly string[] CodigosCerrados =
    [
        "cancelado", "error-microsoft", "configuracion", "url-publica", "solicitud", "canje-tokens", "permisos-microsoft",
        "sin-buzon-exchange", "sin-acceso-compartido", "autenticacion", "solo-administrador", "buzon-compartido", "ya-conectado",
        "compartidos-en-pausa", "suscripcion",
    ];

    // ------------------------------------------------------------------------------------------------------------------
    // Clasificación en el cliente real de Graph
    // ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Un_401_o_403_al_leer_el_perfil_es_SinPermisosDelegados(HttpStatusCode estado)
    {
        var microsoft = new MicrosoftFalso { Perfil = (estado, ErrorGraph("Authorization_RequestDenied", "Insufficient privileges")) };

        var resultado = await Cliente(microsoft).ObtenerBuzonEmailAsync("token", CancellationToken.None);

        resultado.Error.Codigo.Should().Be(CodigosErrorMicrosoft365.SinPermisosDelegados);
    }

    /// <summary>Fuera del 401/403, el perfil ilegible sigue siendo un fallo genérico de la API, aunque el texto hable de permisos.</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Otro_estado_al_leer_el_perfil_no_es_SinPermisosDelegados(HttpStatusCode estado)
    {
        var microsoft = new MicrosoftFalso { Perfil = (estado, ErrorGraph("Authorization_RequestDenied", "Forbidden: insufficient privileges")) };

        var resultado = await Cliente(microsoft).ObtenerBuzonEmailAsync("token", CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.ErrorApi");
    }

    [Fact]
    public async Task Un_404_MailboxNotEnabledForRESTAPI_en_el_buzon_propio_es_SinBuzonExchange()
    {
        var microsoft = new MicrosoftFalso { BandejaPropia = (HttpStatusCode.NotFound, ErrorGraph("MailboxNotEnabledForRESTAPI", "The mailbox is either inactive, soft-deleted, or is hosted on-premise.")) };

        var resultado = await Cliente(microsoft).ObtenerIdentidadBuzonAsync("token", new BuzonGraph("marta@empresa.test", false), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(CodigosErrorMicrosoft365.SinBuzonExchange);
        CodigosErrorMicrosoft365.EsSinAccesoBuzon(resultado.Error).Should().BeTrue("el resto de llamadores lo tratan como sin acceso");
    }

    [Fact]
    public async Task El_mismo_404_en_un_buzon_compartido_es_SinAccesoBuzon()
    {
        var microsoft = new MicrosoftFalso { BandejaCompartida = (HttpStatusCode.NotFound, ErrorGraph("MailboxNotEnabledForRESTAPI", "The mailbox is either inactive.")) };

        var resultado = await Cliente(microsoft).ObtenerIdentidadBuzonAsync("token", new BuzonGraph("cae@empresa.test", true), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(CodigosErrorMicrosoft365.SinAccesoBuzon);
    }

    /// <summary>
    /// Se clasifica por el código de error de Graph y el estado HTTP, nunca por el texto: «MailboxNotEnabledForRESTAPI» dentro del
    /// mensaje de otro código, en un cuerpo que no es JSON o con otro estado no hace de la cuenta una cuenta sin Exchange.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"error":{"code":"ErrorItemNotFound","message":"MailboxNotEnabledForRESTAPI: not found"}}""", CodigosErrorMicrosoft365.SinAccesoBuzon)]
    [InlineData(HttpStatusCode.NotFound, "MailboxNotEnabledForRESTAPI", CodigosErrorMicrosoft365.SinAccesoBuzon)]
    [InlineData(HttpStatusCode.NotFound, """{"error":{"code":"mailboxnotenabledforrestapi"}}""", CodigosErrorMicrosoft365.SinAccesoBuzon)]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":"MailboxNotEnabledForRESTAPI"}}""", CodigosErrorMicrosoft365.SinAccesoBuzon)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"code":"MailboxNotEnabledForRESTAPI"}}""", "Integraciones.Microsoft365.ErrorApi")]
    public async Task El_texto_MailboxNotEnabledForRESTAPI_fuera_de_su_codigo_y_su_estado_no_clasifica(
        HttpStatusCode estado, string cuerpo, string codigoEsperado)
    {
        var microsoft = new MicrosoftFalso { BandejaPropia = (estado, cuerpo) };

        var resultado = await Cliente(microsoft).ObtenerIdentidadBuzonAsync("token", new BuzonGraph("marta@empresa.test", false), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(codigoEsperado);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // GET /integraciones/conectar-microsoft365
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Conectar_con_todo_en_orden_guarda_la_solicitud_y_redirige_a_Microsoft_con_su_state()
    {
        var escenario = new Escenario();
        var clienteId = Guid.NewGuid();

        var contexto = await escenario.ConectarAsync($"?clienteId={clienteId}");

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        var solicitud = escenario.Solicitudes.Agregadas.Should().ContainSingle().Subject;
        solicitud.ClienteId.Should().Be(clienteId);
        solicitud.UsuarioSolicitanteId.Should().Be(escenario.Usuario.UsuarioId!.Value);
        escenario.Unidad.Guardados.Should().Be(1);
        var destino = contexto.Response.Headers.Location.ToString();
        destino.Should().StartWith("https://login.microsoftonline.com/common/oauth2/v2.0/authorize?")
            .And.Contain($"state={solicitud.Id}")
            .And.Contain("redirect_uri=" + Uri.EscapeDataString(UrlPublica + "/integraciones/microsoft365-callback"));
        escenario.Microsoft.Peticiones.Should().BeEmpty("conectar no llama a Microsoft: solo construye la URL");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Conectar_sin_URL_publica_vuelve_a_Conexiones_con_url_publica_y_no_un_JSON(string? urlPublica)
    {
        var escenario = new Escenario();
        escenario.Opciones.UrlPublicaBase = urlPublica;

        var contexto = await escenario.ConectarAsync(string.Empty);

        AfirmarRedireccionCerrada(contexto).Should().Be("url-publica");
        escenario.Solicitudes.Agregadas.Should().BeEmpty();
    }

    /// <summary>Con URL pública pero sin ClientId o sin credencial no se manda a nadie a Microsoft con un client_id vacío.</summary>
    [Fact]
    public async Task Conectar_con_la_configuracion_a_medias_vuelve_con_configuracion()
    {
        var escenario = new Escenario();
        escenario.Opciones.ClientSecret = null;

        var contexto = await escenario.ConectarAsync(string.Empty);

        AfirmarRedireccionCerrada(contexto).Should().Be("configuracion");
        escenario.Solicitudes.Agregadas.Should().BeEmpty();
    }

    [Theory]
    [InlineData(Roles.GestorCae)]
    [InlineData(Roles.CoordinadorCae)]
    [InlineData(Roles.Consulta)]
    [InlineData(null)]
    public async Task Conectar_sin_rol_efectivo_Administrador_vuelve_con_solo_administrador(string? rol)
    {
        var escenario = new Escenario();
        escenario.Usuario.RolEfectivo = rol;

        var contexto = await escenario.ConectarAsync(string.Empty);

        AfirmarRedireccionCerrada(contexto).Should().Be("solo-administrador");
        escenario.Solicitudes.Agregadas.Should().BeEmpty();
    }

    [Theory]
    [InlineData("?buzonCompartido=no-es-una-direccion")]
    [InlineData("?buzonCompartido=Nombre%20%3Ccae%40empresa.test%3E")]
    [InlineData("?buzonCompartido=cae%40empresa.test&gestorPropietarioId=7d1f9a52-5a52-4c1e-9d0a-4f8d9a1b2c3d")]
    [InlineData("?buzonCompartido=cae%40empresa.test&clienteId=7d1f9a52-5a52-4c1e-9d0a-4f8d9a1b2c3d")]
    public async Task Conectar_con_un_compartido_invalido_vuelve_con_buzon_compartido(string query)
    {
        var escenario = new Escenario();

        var contexto = await escenario.ConectarAsync(query);

        AfirmarRedireccionCerrada(contexto).Should().Be("buzon-compartido");
        escenario.Solicitudes.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Conectar_sin_usuario_actual_responde_401_sin_cuerpo()
    {
        var escenario = new Escenario();
        escenario.Usuario.UsuarioId = null;

        var contexto = await escenario.ConectarAsync(string.Empty);

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        contexto.Response.Body.Length.Should().Be(0, "nunca un JSON de problema");
        escenario.Solicitudes.Agregadas.Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------------------------------
    // GET /integraciones/microsoft365-callback
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Callback_con_access_denied_vuelve_con_cancelado_sin_llamar_a_Microsoft()
    {
        var escenario = new Escenario();
        var solicitud = escenario.SolicitudValida();

        var contexto = await escenario.CallbackAsync($"?error=access_denied&state={solicitud.Id}");

        AfirmarRedireccionCerrada(contexto).Should().Be("cancelado");
        escenario.Microsoft.Peticiones.Should().BeEmpty();
        escenario.Mediador.Comandos.Should().BeEmpty();
    }

    /// <summary>El texto del error de Microsoft nunca viaja de vuelta: ni a la URL de destino ni al cuerpo.</summary>
    [Theory]
    [InlineData("?error=server_error&error_description=%3Cscript%3Ealert(1)%3C%2Fscript%3E")]
    [InlineData("?error=%3Cscript%3Ealert(1)%3C%2Fscript%3E")]
    [InlineData("?error=invalid_scope&error_description=AADSTS70011&code=codigo&state=7d1f9a52-5a52-4c1e-9d0a-4f8d9a1b2c3d")]
    [InlineData("?error=access_denied%20&error_uri=https%3A%2F%2Fsitio-ajeno.example%2F")]
    public async Task Callback_con_otro_error_de_OAuth_vuelve_con_error_microsoft_sin_reflejar_nada(string query)
    {
        var escenario = new Escenario();

        var contexto = await escenario.CallbackAsync(query);

        AfirmarRedireccionCerrada(contexto).Should().Be("error-microsoft");
        contexto.Response.Headers.Location.ToString().Should().NotContain("script").And.NotContain("alert")
            .And.NotContain("AADSTS").And.NotContain("sitio-ajeno");
        escenario.Microsoft.Peticiones.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Callback_sin_URL_publica_vuelve_con_url_publica(string? urlPublica)
    {
        var escenario = new Escenario();
        var solicitud = escenario.SolicitudValida();
        escenario.Opciones.UrlPublicaBase = urlPublica;

        var contexto = await escenario.CallbackAsync($"?code=codigo&state={solicitud.Id}");

        AfirmarRedireccionCerrada(contexto).Should().Be("url-publica");
        escenario.Microsoft.Peticiones.Should().BeEmpty();
        escenario.Solicitudes.Eliminadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Callback_con_la_configuracion_a_medias_vuelve_con_configuracion()
    {
        var escenario = new Escenario();
        var solicitud = escenario.SolicitudValida();
        escenario.Opciones.ClientId = null;

        var contexto = await escenario.CallbackAsync($"?code=codigo&state={solicitud.Id}");

        AfirmarRedireccionCerrada(contexto).Should().Be("configuracion");
        escenario.Microsoft.Peticiones.Should().BeEmpty();
    }

    [Theory]
    [InlineData("?state=7d1f9a52-5a52-4c1e-9d0a-4f8d9a1b2c3d")]
    [InlineData("?code=codigo")]
    [InlineData("?code=%20&state=7d1f9a52-5a52-4c1e-9d0a-4f8d9a1b2c3d")]
    [InlineData("?code=codigo&state=no-es-un-guid")]
    [InlineData("?code=codigo&state=%3Cscript%3Ealert(1)%3C%2Fscript%3E")]
    [InlineData("?code=codigo&state=7d1f9a52-5a52-4c1e-9d0a-4f8d9a1b2c3d")]
    public async Task Callback_sin_code_o_state_valido_vuelve_con_solicitud(string query)
    {
        var escenario = new Escenario();

        var contexto = await escenario.CallbackAsync(query);

        AfirmarRedireccionCerrada(contexto).Should().Be("solicitud");
        escenario.Microsoft.Peticiones.Should().BeEmpty();
    }

    /// <summary>El state de otra persona, o uno caducado, no se consume ni canjea nada (account-linking CSRF).</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Callback_con_una_solicitud_ajena_o_caducada_vuelve_con_solicitud(bool ajena, bool caducada)
    {
        var escenario = new Escenario();
        var solicitud = escenario.SolicitudValida(
            usuarioId: ajena ? Guid.NewGuid() : null,
            creadaUtc: caducada ? DateTime.UtcNow - SolicitudConexionMicrosoft365.DuracionValidez - TimeSpan.FromMinutes(1) : null);

        var contexto = await escenario.CallbackAsync($"?code=codigo&state={solicitud.Id}");

        AfirmarRedireccionCerrada(contexto).Should().Be("solicitud");
        escenario.Solicitudes.Eliminadas.Should().BeEmpty();
        escenario.Microsoft.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task Callback_sin_usuario_actual_vuelve_con_solicitud()
    {
        var escenario = new Escenario();
        var solicitud = escenario.SolicitudValida();
        escenario.Usuario.UsuarioId = null;

        var contexto = await escenario.CallbackAsync($"?code=codigo&state={solicitud.Id}");

        AfirmarRedireccionCerrada(contexto).Should().Be("solicitud");
        escenario.Microsoft.Peticiones.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Callback_con_el_canje_fallido_vuelve_con_canje_tokens_y_el_state_queda_consumido(HttpStatusCode estado)
    {
        var escenario = new Escenario();
        var solicitud = escenario.SolicitudValida();
        escenario.Microsoft.TokenDelegado = (estado, """{"error":"invalid_grant","error_description":"AADSTS54005"}""");

        var contexto = await escenario.CallbackAsync($"?code=codigo&state={solicitud.Id}");

        AfirmarRedireccionCerrada(contexto).Should().Be("canje-tokens");
        escenario.Solicitudes.Eliminadas.Should().ContainSingle().Which.Should().BeSameAs(solicitud);
        escenario.Unidad.PeticionesAlGuardar.Should().Equal([0], "el state se consume y se guarda antes de llamar a Microsoft");
        escenario.Mediador.Comandos.Should().BeEmpty();

        var peticionesAntes = escenario.Microsoft.Peticiones.Count;
        var segundo = await escenario.CallbackAsync($"?code=otro-codigo&state={solicitud.Id}");

        AfirmarRedireccionCerrada(segundo).Should().Be("solicitud", "un state consumido no vale para un segundo intento");
        escenario.Microsoft.Peticiones.Should().HaveCount(peticionesAntes);
    }

    /// <summary>
    /// Lo que el cliente Graph no traduce a un Result (el límite de tiempo o el circuito abierto de la resiliencia del cliente
    /// tipado, un perfil que no es JSON) vuelve con el aviso de autenticación, con el state ya consumido, y nunca con un 500.
    /// </summary>
    [Theory]
    [InlineData("timeout-canje")]
    [InlineData("circuito-perfil")]
    [InlineData("perfil-no-json")]
    public async Task Callback_con_una_excepcion_de_Microsoft_vuelve_con_autenticacion_y_no_con_un_500(string caso)
    {
        var escenario = new Escenario();
        var solicitud = escenario.SolicitudValida();
        switch (caso)
        {
            case "timeout-canje": escenario.Microsoft.FalloCanje = new Polly.Timeout.TimeoutRejectedException(); break;
            case "circuito-perfil": escenario.Microsoft.FalloPerfil = new Polly.CircuitBreaker.BrokenCircuitException(); break;
            default: escenario.Microsoft.Perfil = (HttpStatusCode.OK, "<html>no es json</html>"); break;
        }

        var contexto = await escenario.CallbackAsync($"?code=codigo&state={solicitud.Id}");

        AfirmarRedireccionCerrada(contexto).Should().Be("autenticacion");
        escenario.Unidad.Guardados.Should().Be(1);
        escenario.Solicitudes.Guardadas.Should().NotContain(solicitud);
        escenario.Mediador.Comandos.Should().BeEmpty();
    }

    [Fact]
    public async Task Callback_que_no_puede_consumir_el_state_vuelve_con_solicitud_sin_llamar_a_Microsoft()
    {
        var escenario = new Escenario();
        var solicitud = escenario.SolicitudValida();
        escenario.Unidad.Fallo = new InvalidOperationException("otro callback consumió antes este state");

        var contexto = await escenario.CallbackAsync($"?code=codigo&state={solicitud.Id}");

        AfirmarRedireccionCerrada(contexto).Should().Be("solicitud");
        escenario.Microsoft.Peticiones.Should().BeEmpty();
        escenario.Mediador.Comandos.Should().BeEmpty();
    }

    /// <summary>Cada fallo de la preparación del alta vuelve con su código: solo lo que no tiene otro cae en «autenticacion».</summary>
    [Theory]
    [InlineData("perfil 403", false, "permisos-microsoft")]
    [InlineData("perfil 401", false, "permisos-microsoft")]
    [InlineData("perfil 500", false, "autenticacion")]
    [InlineData("perfil sin dirección", false, "autenticacion")]
    [InlineData("propio sin Exchange", false, "sin-buzon-exchange")]
    [InlineData("propio 404 con el texto en otro código", false, "autenticacion")]
    [InlineData("propio 503", false, "autenticacion")]
    [InlineData("compartido sin token de aplicación", true, "sin-acceso-compartido")]
    [InlineData("compartido fuera del ámbito", true, "sin-acceso-compartido")]
    [InlineData("compartido sin Exchange", true, "sin-acceso-compartido")]
    [InlineData("compartido 500", true, "autenticacion")]
    [InlineData("compartido con token sin directorio", true, "autenticacion")]
    public async Task Callback_con_un_fallo_del_alta_vuelve_con_su_codigo(string caso, bool compartido, string codigo)
    {
        var escenario = new Escenario();
        var solicitud = escenario.SolicitudValida(buzonCompartido: compartido ? "cae@empresa.test" : null);
        PrepararFalloDeAlta(caso, escenario.Microsoft);

        var contexto = await escenario.CallbackAsync($"?code=codigo&state={solicitud.Id}");

        AfirmarRedireccionCerrada(contexto).Should().Be(codigo, caso);
        escenario.Mediador.Comandos.Should().BeEmpty();
    }

    private static void PrepararFalloDeAlta(string caso, MicrosoftFalso m)
    {
        switch (caso)
        {
            case "perfil 403": m.Perfil = (HttpStatusCode.Forbidden, ErrorGraph("Authorization_RequestDenied", "x")); break;
            case "perfil 401": m.Perfil = (HttpStatusCode.Unauthorized, ErrorGraph("InvalidAuthenticationToken", "x")); break;
            case "perfil 500": m.Perfil = (HttpStatusCode.InternalServerError, "{}"); break;
            case "perfil sin dirección": m.Perfil = (HttpStatusCode.OK, "{}"); break;
            case "propio sin Exchange": m.BandejaPropia = (HttpStatusCode.NotFound, ErrorGraph("MailboxNotEnabledForRESTAPI", "x")); break;
            case "propio 404 con el texto en otro código":
                m.BandejaPropia = (HttpStatusCode.NotFound, ErrorGraph("ErrorItemNotFound", "MailboxNotEnabledForRESTAPI")); break;
            case "propio 503": m.BandejaPropia = (HttpStatusCode.ServiceUnavailable, "{}"); break;
            case "compartido sin token de aplicación": m.TokenAplicacion = (HttpStatusCode.BadRequest, """{"error":"invalid_client"}"""); break;
            case "compartido fuera del ámbito": m.BandejaCompartida = (HttpStatusCode.Forbidden, ErrorGraph("ErrorAccessDenied", "x")); break;
            case "compartido sin Exchange": m.BandejaCompartida = (HttpStatusCode.NotFound, ErrorGraph("MailboxNotEnabledForRESTAPI", "x")); break;
            case "compartido 500": m.BandejaCompartida = (HttpStatusCode.InternalServerError, "{}"); break;
            case "compartido con token sin directorio": m.TokenDelegado = (HttpStatusCode.OK, TokensDelegados(TokenSinRoles)); break;
            default: throw new ArgumentOutOfRangeException(nameof(caso), caso, "Caso no previsto.");
        }
    }

    [Fact]
    public async Task Callback_con_todo_en_orden_conecta_el_buzon_propio_y_vuelve_con_conectado()
    {
        var escenario = new Escenario();
        var clienteId = Guid.NewGuid();
        var solicitud = escenario.SolicitudValida(clienteId: clienteId);

        var contexto = await escenario.CallbackAsync($"?code=codigo&state={solicitud.Id}");

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        contexto.Response.Headers.Location.ToString().Should().Be("/integraciones?conectado=true");
        escenario.Solicitudes.Eliminadas.Should().ContainSingle();
        var comando = escenario.Mediador.Comandos.Should().ContainSingle().Subject;
        comando.BuzonEmail.Should().Be("marta@empresa.test");
        comando.ClienteId.Should().Be(clienteId);
        comando.IdentidadBuzon.Should().Be("inbox-propio");
        comando.RefreshToken.Should().Be("refresco");
        comando.NotificationUrlBase.Should().Be(UrlPublica);
        comando.ConsentidoPorUsuarioId.Should().Be(escenario.Usuario.UsuarioId);
    }

    [Fact]
    public async Task Callback_con_todo_en_orden_conecta_el_compartido_con_el_token_de_aplicacion_y_sin_refresh_token()
    {
        var escenario = new Escenario();
        var solicitud = escenario.SolicitudValida(buzonCompartido: "cae@empresa.test");

        var contexto = await escenario.CallbackAsync($"?code=codigo&state={solicitud.Id}");

        contexto.Response.Headers.Location.ToString().Should().Be("/integraciones?conectado=true");
        var comando = escenario.Mediador.Comandos.Should().ContainSingle().Subject;
        comando.BuzonEmail.Should().Be("cae@empresa.test");
        comando.RefreshToken.Should().BeNull();
        comando.IdentidadBuzon.Should().Be("inbox-compartido");
        comando.DirectorioEntraId.Should().Be(escenario.Microsoft.Directorio.ToString("D"));
    }

    [Fact]
    public async Task Callback_cuyo_comando_rechaza_el_buzon_ya_conectado_vuelve_con_ya_conectado()
    {
        var escenario = new Escenario();
        var solicitud = escenario.SolicitudValida();
        escenario.Mediador.Respuesta = Result.Fallo<Guid>(Error.Crear(ConectarBuzonMicrosoft365CommandHandler.CodigoBuzonYaConectado, "x"));

        var contexto = await escenario.CallbackAsync($"?code=codigo&state={solicitud.Id}");

        AfirmarRedireccionCerrada(contexto).Should().Be("ya-conectado");
    }

    [Fact]
    public async Task Callback_cuyo_comando_no_valida_vuelve_con_solicitud_y_no_con_un_500()
    {
        var escenario = new Escenario();
        var solicitud = escenario.SolicitudValida();
        escenario.Mediador.ExcepcionDeValidacion = true;

        var contexto = await escenario.CallbackAsync($"?code=codigo&state={solicitud.Id}");

        AfirmarRedireccionCerrada(contexto).Should().Be("solicitud");
    }

    /// <summary>Toda ruta de error que el endpoint declara es local y lleva un código de la lista cerrada, que Conexiones conoce.</summary>
    [Fact]
    public void Toda_redireccion_de_error_declarada_es_local_y_de_la_lista_cerrada()
    {
        var redirecciones = typeof(ConectarMicrosoft365Endpoints)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.StartsWith("Redireccion", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        redirecciones.Should().HaveCountGreaterThanOrEqualTo(10, "control positivo: la reflexión encuentra las constantes");
        redirecciones.Should().OnlyContain(r => r.StartsWith(RaizRedireccionError, StringComparison.Ordinal));
        redirecciones.Select(r => r[RaizRedireccionError.Length..]).Should().OnlyContain(c => CodigosCerrados.Contains(c));
    }

    private static string AfirmarRedireccionCerrada(HttpContext contexto)
    {
        contexto.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        contexto.Response.Body.Length.Should().Be(0, "nunca un JSON de problema en el navegador");
        var destino = contexto.Response.Headers.Location.ToString();
        destino.Should().StartWith(RaizRedireccionError, "redirección local, sin open redirect");
        var codigo = destino[RaizRedireccionError.Length..];
        CodigosCerrados.Should().Contain(codigo);
        return codigo;
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Dobles
    // ------------------------------------------------------------------------------------------------------------------

    private static Microsoft365GraphClient Cliente(MicrosoftFalso microsoft, Microsoft365GraphOptions? opciones = null) => new(
        new HttpClient(microsoft),
        Options.Create(opciones ?? new Microsoft365GraphOptions { ClientId = "cliente", ClientSecret = "secreto", UrlPublicaBase = UrlPublica }),
        NullLogger<Microsoft365GraphClient>.Instance);

    private static string ErrorGraph(string codigo, string mensaje) =>
        $$$"""{"error":{"code":"{{{codigo}}}","message":"{{{mensaje}}}"}}""";

    /// <summary>Token sin «roles» (como uno de aplicación acotado en Exchange) y sin «tid».</summary>
    private static readonly string TokenSinRoles = TokenJwt("""{"aud":"graph"}""");

    private static string TokenJwt(string carga)
    {
        static string Base64Url(string texto) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(texto)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{Base64Url("{\"alg\":\"none\"}")}.{Base64Url(carga)}.firma";
    }

    private static string TokensDelegados(string accessToken) =>
        $$"""{"access_token":"{{accessToken}}","refresh_token":"refresco","expires_in":3599}""";

    /// <summary>Entra ID y Graph de mentira: cada ruta responde lo que el test fije; una petición no prevista rompe el test.</summary>
    public sealed class MicrosoftFalso : HttpMessageHandler
    {
        /// <summary>Directorio del token delegado; distinto en cada test para no compartir la caché de tokens de aplicación.</summary>
        public Guid Directorio { get; } = Guid.NewGuid();

        public (HttpStatusCode Estado, string Cuerpo) TokenDelegado { get; set; }
        public (HttpStatusCode Estado, string Cuerpo) TokenAplicacion { get; set; } =
            (HttpStatusCode.OK, $$"""{"access_token":"{{TokenSinRoles}}","expires_in":3599}""");
        public (HttpStatusCode Estado, string Cuerpo) Perfil { get; set; } = (HttpStatusCode.OK, """{"mail":"marta@empresa.test"}""");
        public (HttpStatusCode Estado, string Cuerpo) BandejaPropia { get; set; } = (HttpStatusCode.OK, """{"id":"inbox-propio"}""");
        public (HttpStatusCode Estado, string Cuerpo) BandejaCompartida { get; set; } = (HttpStatusCode.OK, """{"id":"inbox-compartido"}""");

        public List<string> Peticiones { get; } = [];

        /// <summary>Excepción que lanza el canje del código (la que lanzaría la resiliencia del cliente tipado).</summary>
        public Exception? FalloCanje { get; set; }

        public Exception? FalloPerfil { get; set; }

        public MicrosoftFalso()
        {
            TokenDelegado = (HttpStatusCode.OK, TokensDelegados(TokenJwt($$"""{"tid":"{{Directorio:D}}"}""")));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            Peticiones.Add(url.AbsoluteUri);
            if (FalloCanje is not null && url.Host == "login.microsoftonline.com" && url.AbsolutePath.StartsWith("/common/", StringComparison.Ordinal))
                throw FalloCanje;
            if (FalloPerfil is not null && url.Host == "graph.microsoft.com" && url.AbsolutePath == "/v1.0/me")
                throw FalloPerfil;
            var (estado, cuerpo) = url.Host switch
            {
                "login.microsoftonline.com" when url.AbsolutePath.StartsWith("/common/", StringComparison.Ordinal) => TokenDelegado,
                "login.microsoftonline.com" => TokenAplicacion,
                "graph.microsoft.com" when url.AbsolutePath == "/v1.0/me" => Perfil,
                "graph.microsoft.com" when url.AbsolutePath.StartsWith("/v1.0/me/", StringComparison.Ordinal) => BandejaPropia,
                "graph.microsoft.com" when url.AbsolutePath.StartsWith("/v1.0/users/", StringComparison.Ordinal) => BandejaCompartida,
                _ => throw new InvalidOperationException($"Petición no prevista a {url}"),
            };
            return Task.FromResult(new HttpResponseMessage(estado) { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Escenario
    {
        public Microsoft365GraphOptions Opciones { get; } = new() { ClientId = "cliente", ClientSecret = "secreto", UrlPublicaBase = UrlPublica };
        public MicrosoftFalso Microsoft { get; } = new();
        public SolicitudesFalsas Solicitudes { get; } = new();
        public UnidadDeTrabajoFalsa Unidad { get; } = new();
        public UsuarioActualFalso Usuario { get; } = new();
        public MediadorConectar Mediador { get; } = new();

        public Escenario()
        {
            Unidad.AlGuardar = () =>
            {
                Unidad.PeticionesAlGuardar.Add(Microsoft.Peticiones.Count);
                Solicitudes.Confirmar();
            };
        }

        public SolicitudConexionMicrosoft365 SolicitudValida(
            Guid? usuarioId = null, DateTime? creadaUtc = null, string? buzonCompartido = null, Guid? clienteId = null)
        {
            var solicitud = new SolicitudConexionMicrosoft365(
                usuarioId ?? Usuario.UsuarioId!.Value, clienteId, null, creadaUtc ?? DateTime.UtcNow, buzonCompartido);
            Solicitudes.Guardadas.Add(solicitud);
            return solicitud;
        }

        public Task<HttpContext> ConectarAsync(string query) => InvocarAsync("/integraciones/conectar-microsoft365", query);

        public Task<HttpContext> CallbackAsync(string query) => InvocarAsync("/integraciones/microsoft365-callback", query);

        private async Task<HttpContext> InvocarAsync(string ruta, string query)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Services.AddSingleton<IOptions<Microsoft365GraphOptions>>(Options.Create(Opciones));
            builder.Services.AddSingleton<IMicrosoft365GraphClient>(Cliente(Microsoft, Opciones));
            builder.Services.AddSingleton<ISolicitudConexionMicrosoft365Repository>(Solicitudes);
            builder.Services.AddSingleton<IUnitOfWork>(Unidad);
            builder.Services.AddSingleton<ICurrentUserService>(Usuario);
            builder.Services.AddSingleton<IMediator>(Mediador);
            builder.Services.AddSingleton<AltaBuzonMicrosoft365Service>();
            await using var app = builder.Build();
            app.MapConectarMicrosoft365Endpoints();
            var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
                .Single(e => e.RoutePattern.RawText == ruta);

            using var scope = app.Services.CreateScope();
            var contexto = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            contexto.Request.Method = HttpMethods.Get;
            contexto.Request.QueryString = new QueryString(query);
            contexto.Response.Body = new MemoryStream();
            await endpoint.RequestDelegate!(contexto);
            return contexto;
        }
    }

    private sealed class SolicitudesFalsas : ISolicitudConexionMicrosoft365Repository
    {
        public List<SolicitudConexionMicrosoft365> Guardadas { get; } = [];
        public List<SolicitudConexionMicrosoft365> Agregadas { get; } = [];
        public List<SolicitudConexionMicrosoft365> Eliminadas { get; } = [];

        public Task<SolicitudConexionMicrosoft365?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Guardadas.SingleOrDefault(s => s.Id == id));

        public void Agregar(SolicitudConexionMicrosoft365 solicitud) => Agregadas.Add(solicitud);

        public void Eliminar(SolicitudConexionMicrosoft365 solicitud) => Eliminadas.Add(solicitud);

        /// <summary>Lo que hace el guardado: la solicitud eliminada deja de encontrarse.</summary>
        public void Confirmar() => Guardadas.RemoveAll(Eliminadas.Contains);
    }

    private sealed class UnidadDeTrabajoFalsa : IUnitOfWork
    {
        public int Guardados { get; private set; }

        /// <summary>Peticiones hechas a Microsoft en el momento de cada guardado.</summary>
        public List<int> PeticionesAlGuardar { get; } = [];

        public Action? AlGuardar { get; set; }

        public Exception? Fallo { get; set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (Fallo is not null) throw Fallo;
            Guardados++;
            AlGuardar?.Invoke();
            return Task.FromResult(1);
        }
    }

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Guid? UsuarioId { get; set; } = Guid.NewGuid();
        public string? RolEfectivo { get; set; } = Roles.Administrador;

        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(UsuarioId);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(RolEfectivo);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult(RolEfectivo);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class MediadorConectar : IMediator
    {
        public List<ConectarBuzonMicrosoft365Command> Comandos { get; } = [];
        public Result<Guid> Respuesta { get; set; } = Result.Exito(Guid.NewGuid());
        public bool ExcepcionDeValidacion { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is not ConectarBuzonMicrosoft365Command comando)
                throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");

            Comandos.Add(comando);
            if (ExcepcionDeValidacion)
                throw new FluentValidation.ValidationException("combinación rechazada");
            return Task.FromResult((TResponse)(object)Respuesta);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
