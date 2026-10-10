using CaeManager.Web.Components.Account;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// LV-2: una sesión que caduca con la página abierta convierte el siguiente POST de
/// formulario (cambiar de Tenant activo, cerrar sesión, cambiar de idioma…) en un desafío de
/// autenticación. El handler de cookies guardaba la ruta de ese POST como <c>ReturnUrl</c>, y
/// al volver a entrar el navegador la pedía por GET: un 404 en vez de la aplicación.
///
/// <para>
/// Se observa con el <c>CookieAuthenticationHandler</c> real del framework desafiando una
/// petición, no con una copia de su lógica: el <c>Location</c> que sale es el que recibe el
/// navegador.
/// </para>
///
/// <para>
/// Límite del instrumento: el proyecto no expone un <c>WebApplicationFactory</c>, así que
/// este test no ve que <c>Program.cs</c> llame a
/// <see cref="OmitirReturnUrlEnPeticionesNoNavegables.Configurar"/> ni que la política de
/// autorización por defecto desafíe a esos endpoints; lo primero lo vigila
/// <c>ReturnUrlSoloNavegableRegistradoEnProgramTests</c> en Architecture.Tests.
/// </para>
/// </summary>
public class OmitirReturnUrlEnPeticionesNoNavegablesTests
{
    private const string Esquema = "Identity.Application";
    private const string Acceso = "https://app.ejemplo.test/cuenta/iniciar-sesion";

    /// <summary>Los endpoints de la aplicación que solo admiten POST y exigen sesión.</summary>
    public static IEnumerable<object[]> RutasSoloPost() =>
        new[]
        {
            "/cuenta/cliente-activo",
            "/cuenta/cerrar-sesion",
            "/cuenta/idioma",
            "/cuenta/soporte/abrir",
            "/cuenta/soporte/salir",
            "/cuenta/soporte/cerrar",
            "/cuenta/vista-vocabulario",
            "/cuenta/vista-demo",
            "/cuenta/extension/token",
        }.Select(ruta => new object[] { ruta });

    [Theory]
    [MemberData(nameof(RutasSoloPost))]
    public async Task El_desafio_de_un_POST_lleva_al_acceso_sin_ReturnUrl_y_el_inicio_de_sesion_al_destino_por_defecto(string ruta)
    {
        var respuesta = await DesafiarAsync("POST", ruta, configurar: OmitirReturnUrlEnPeticionesNoNavegables.Configurar);

        respuesta.StatusCode.Should().Be(StatusCodes.Status302Found);
        var destino = respuesta.Headers.Location.ToString();
        destino.Should().Be(Acceso, "la ruta de un POST no es un sitio al que el navegador pueda volver con un GET");

        // Lo que ve el usuario: sin ReturnUrl, el inicio de sesión aterriza en Inicio con la
        // marca de un solo uso (D-2), no en la ruta del formulario.
        RedireccionLocal.DestinoTrasLogin(ReturnUrlDe(destino)).Should().Be("/?desde=login");
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Cualquier_metodo_que_no_sea_GET_ni_HEAD_pierde_el_ReturnUrl(string metodo)
    {
        var respuesta = await DesafiarAsync(metodo, "/cuenta/cliente-activo", configurar: OmitirReturnUrlEnPeticionesNoNavegables.Configurar);

        respuesta.Headers.Location.ToString().Should().Be(Acceso);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task El_desafio_de_una_navegacion_conserva_el_ReturnUrl_con_su_consulta(string metodo)
    {
        var respuesta = await DesafiarAsync(
            metodo, "/documentos", "?filtro=urgente&pestana=plataforma",
            configurar: OmitirReturnUrlEnPeticionesNoNavegables.Configurar);

        respuesta.StatusCode.Should().Be(StatusCodes.Status302Found);
        var retorno = ReturnUrlDe(respuesta.Headers.Location.ToString());
        retorno.Should().Be("/documentos?filtro=urgente&pestana=plataforma");
        RedireccionLocal.DestinoTrasLogin(retorno).Should().Be("/documentos?filtro=urgente&pestana=plataforma");
    }

    /// <summary>
    /// Control positivo del instrumento y reproducción del defecto: con los eventos por defecto
    /// del framework (lo que había antes del cambio) el mismo arnés SÍ ve la ruta del POST
    /// guardada como <c>ReturnUrl</c>, y el inicio de sesión la respeta. Sin este caso, un verde
    /// arriba podría ser un arnés incapaz de ver el <c>ReturnUrl</c>.
    /// </summary>
    [Fact]
    public async Task Con_los_eventos_por_defecto_del_framework_el_POST_si_guarda_su_ruta_como_ReturnUrl()
    {
        var respuesta = await DesafiarAsync("POST", "/cuenta/cliente-activo", configurar: _ => { });

        var destino = respuesta.Headers.Location.ToString();
        destino.Should().Be(Acceso + "?ReturnUrl=%2Fcuenta%2Fcliente-activo");
        RedireccionLocal.DestinoTrasLogin(ReturnUrlDe(destino)).Should().Be("/cuenta/cliente-activo");
    }

    /// <summary>
    /// Un desafío con <c>RedirectUri</c> explícito lo decidió código de la aplicación, no la
    /// ruta de la petición: se respeta también en un POST.
    /// </summary>
    [Fact]
    public async Task Un_RedirectUri_explicito_se_respeta_aunque_la_peticion_sea_un_POST()
    {
        var respuesta = await DesafiarAsync(
            "POST", "/cuenta/cliente-activo",
            configurar: OmitirReturnUrlEnPeticionesNoNavegables.Configurar,
            propiedades: new AuthenticationProperties { RedirectUri = "/documentos" });

        ReturnUrlDe(respuesta.Headers.Location.ToString()).Should().Be("/documentos");
    }

    /// <summary>
    /// El envoltorio no decide cómo se responde: una petición AJAX sigue recibiendo el 401 del
    /// evento original, no una redirección.
    /// </summary>
    [Fact]
    public async Task Una_peticion_AJAX_sigue_recibiendo_401_del_evento_original()
    {
        var respuesta = await DesafiarAsync(
            "POST", "/cuenta/cliente-activo",
            configurar: OmitirReturnUrlEnPeticionesNoNavegables.Configurar,
            ajustar: peticion => peticion.Headers.XRequestedWith = "XMLHttpRequest");

        respuesta.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        respuesta.Headers.Location.ToString().Should().Be(Acceso);
    }

    private static string? ReturnUrlDe(string destino)
    {
        var consulta = new Uri(destino).Query;
        return QueryHelpers.ParseQuery(consulta).TryGetValue("ReturnUrl", out var valor) ? valor.ToString() : null;
    }

    private static async Task<HttpResponse> DesafiarAsync(
        string metodo, string ruta, string consulta = "",
        Action<CookieAuthenticationOptions>? configurar = null,
        Action<HttpRequest>? ajustar = null,
        AuthenticationProperties? propiedades = null)
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddAuthentication(Esquema).AddCookie(Esquema, opciones =>
        {
            // La misma ruta de acceso que fija Program.cs en ConfigureApplicationCookie.
            opciones.LoginPath = "/cuenta/iniciar-sesion";
            configurar?.Invoke(opciones);
        });
        await using var proveedor = servicios.BuildServiceProvider();

        var contexto = new DefaultHttpContext { RequestServices = proveedor };
        contexto.Request.Method = metodo;
        contexto.Request.Scheme = "https";
        contexto.Request.Host = new HostString("app.ejemplo.test");
        contexto.Request.Path = ruta;
        contexto.Request.QueryString = new QueryString(consulta);
        ajustar?.Invoke(contexto.Request);

        await contexto.ChallengeAsync(Esquema, propiedades);

        contexto.Response.Headers.Location.ToString().Should().StartWith(
            Acceso, "el instrumento tiene que ver la redirección al acceso para poder afirmar nada sobre su ReturnUrl");
        return contexto.Response;
    }
}
