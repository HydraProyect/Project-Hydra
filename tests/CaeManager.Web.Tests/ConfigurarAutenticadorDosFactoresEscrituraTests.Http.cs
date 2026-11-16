using System.Globalization;
using System.Net;
using System.Security.Claims;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Account;
using CaeManager.Web.Components.Account.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// #2125 por HTTP y con la página real en renderizado estático: cada POST construye una instancia nueva del
/// componente, así que lo que la reconstruye con la prueba de reautenticación ya recibida (la rama
/// <c>pruebaRecibida</c> de la carga) solo se observa aquí. En bUnit la instancia sobrevive entre envíos y
/// conserva la clave del POST de «continuar», de modo que ninguna prueba bUnit nota si esa rama deja de
/// cargarla. Mismo arnés que <see cref="RechazoDeAntiforgeryEnPostDeCuentaTests"/> (Kestrel en
/// 127.0.0.1, cookie de autenticación y <c>UseAntiforgery</c>), con los dobles de esta clase detrás.
/// </summary>
public partial class ConfigurarAutenticadorDosFactoresEscrituraTests
{
    private const string RutaConfigurar = "/cuenta/configurar-2fa";

    /// <summary>
    /// GET sin clave, POST de «continuar» que la enseña, código erróneo con la prueba válida que vuelve a
    /// enseñar la misma clave para reintentar, y reintento correcto que activa el factor.
    /// </summary>
    [Fact]
    public async Task Por_HTTP_un_codigo_erroneo_con_la_prueba_recibida_vuelve_a_ensenar_la_clave_y_el_reintento_activa()
    {
        await using var app = await ArrancarPaginaRealAsync();
        using var cliente = ClienteConCookies(app);
        (await cliente.GetAsync("/entrar")).EnsureSuccessStatusCode();

        var get = await LeerAsync(await cliente.GetAsync(RutaConfigurar));
        get.QuerySelectorAll(".clave-manual, img.qr-2fa, #codigo, #prueba-reautenticacion").Should().BeEmpty(
            "control: un GET no lleva la clave");
        _almacen.ClaveGuardada.Should().BeNull("control: un GET no genera la clave");

        var trasContinuar = await EnviarAsync(cliente, get, "mostrar-clave-2fa");
        var clave = _almacen.ClaveGuardada;
        clave.Should().NotBeNullOrEmpty("el POST de continuar genera la clave");
        var claveFormateada = ClaveAutenticadorTotp.FormatearClave(clave!);
        trasContinuar.QuerySelector(".clave-manual code")!.TextContent.Trim().Should().Be(claveFormateada);
        trasContinuar.QuerySelector("#prueba-reautenticacion")!.GetAttribute("value").Should().NotBeNullOrEmpty();

        // Instancia nueva con la prueba en el cuerpo. Si la rama pruebaRecibida no cargara la clave, el formulario
        // del código no existiría en esta instancia y Blazor rechazaría el POST con 400 antes de verificar nada:
        // el alta real quedaría rota incluso con el código correcto.
        var trasError = await EnviarAsync(cliente, trasContinuar, "configurar-2fa", codigo: "000000");

        trasError.QuerySelectorAll(".tarjeta-mostrar-clave").Should().BeEmpty(
            "la respuesta al código erróneo con prueba válida vuelve a enseñar la clave, no la tarjeta de continuar");
        trasError.QuerySelector(".clave-manual code")!.TextContent.Trim().Should().Be(claveFormateada,
            "una instancia nueva con la prueba recibida vuelve a cargar la misma clave para reintentar");
        trasError.QuerySelector(".tarjeta-confirmacion [role=alert]")!.TextContent.Should().Contain("El código no es válido");
        trasError.QuerySelector("#prueba-reautenticacion")!.GetAttribute("value").Should().NotBeNullOrEmpty(
            "la prueba sigue viajando con el reintento");
        _almacen.ClaveGuardada.Should().Be(clave, "el reintento no rota la clave");
        _almacen.DosFactoresActivo.Should().BeFalse();

        var trasReintento = await EnviarAsync(cliente, trasError, "configurar-2fa", codigo: GestorUsuariosFallaAVoluntad.CodigoValido);

        _almacen.DosFactoresActivo.Should().BeTrue("el reintento correcto con la misma prueba activa el factor");
        trasReintento.QuerySelectorAll(".lista-codigos code").Should().NotBeEmpty();
        _almacen.ClaveGuardada.Should().Be(clave);
    }

    /// <summary>
    /// Sin antiforgery ni el POST de «continuar» ni el del código pasan: 400, sin la clave en la respuesta y sin
    /// activar el factor, aunque el del código lleve una prueba válida y el código correcto.
    /// </summary>
    [Fact]
    public async Task Por_HTTP_sin_antiforgery_ni_continuar_ni_el_codigo_ensenan_la_clave_ni_activan()
    {
        await using var app = await ArrancarPaginaRealAsync();
        using var cliente = ClienteConCookies(app);
        (await cliente.GetAsync("/entrar")).EnsureSuccessStatusCode();
        var get = await LeerAsync(await cliente.GetAsync(RutaConfigurar));

        using (var sinTokenContinuar = await PostarAsync(cliente, get, "mostrar-clave-2fa", sinAntiforgery: true))
        {
            sinTokenContinuar.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await sinTokenContinuar.Content.ReadAsStringAsync()).Should().NotContain("otpauth");
        }
        _almacen.ClaveGuardada.Should().BeNull("un POST sin antiforgery no genera la clave");

        var trasContinuar = await EnviarAsync(cliente, get, "mostrar-clave-2fa");
        var clave = _almacen.ClaveGuardada!;

        using var sinTokenCodigo = await PostarAsync(cliente, trasContinuar, "configurar-2fa",
            codigo: GestorUsuariosFallaAVoluntad.CodigoValido, sinAntiforgery: true);

        sinTokenCodigo.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var cuerpo = await sinTokenCodigo.Content.ReadAsStringAsync();
        cuerpo.Should().NotContain(clave).And.NotContain(ClaveAutenticadorTotp.FormatearClave(clave)).And.NotContain("otpauth");
        _almacen.DosFactoresActivo.Should().BeFalse("sin antiforgery el código correcto con prueba válida no activa nada");
    }

    private static async Task<IDocument> EnviarAsync(HttpClient cliente, IDocument pagina, string formulario, string? codigo = null)
    {
        using var respuesta = await PostarAsync(cliente, pagina, formulario, codigo);
        respuesta.StatusCode.Should().Be(HttpStatusCode.OK, $"el POST de {formulario} con antiforgery se atiende");
        return await LeerAsync(respuesta);
    }

    /// <summary>Envía el formulario tal como lo pintó la respuesta anterior: sus campos con nombre y su action.</summary>
    private static Task<HttpResponseMessage> PostarAsync(
        HttpClient cliente, IDocument pagina, string formulario, string? codigo = null, bool sinAntiforgery = false)
    {
        var form = pagina.QuerySelectorAll("form")
            .Should().ContainSingle(f => f.QuerySelector($"input[name='_handler'][value='{formulario}']") != null,
                $"la respuesta anterior ofrece el formulario {formulario}").Subject;
        var campos = form.QuerySelectorAll("input[name]")
            .ToDictionary(i => i.GetAttribute("name")!, i => i.GetAttribute("value") ?? string.Empty);
        if (codigo is not null)
            campos[form.QuerySelector("#codigo")!.GetAttribute("name")!] = codigo;
        if (sinAntiforgery)
            campos.Remove("__RequestVerificationToken").Should().BeTrue("control: el formulario llevaba antiforgery");
        var accion = new Uri(cliente.BaseAddress!, form.GetAttribute("action") ?? RutaConfigurar);
        return cliente.PostAsync(accion, new FormUrlEncodedContent(campos));
    }

    private static async Task<IDocument> LeerAsync(HttpResponseMessage respuesta)
    {
        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        return new HtmlParser().ParseDocument(await respuesta.Content.ReadAsStringAsync());
    }

    private static HttpClient ClienteConCookies(WebApplication app) =>
        new(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() })
        {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()),
        };

    private async Task<WebApplication> ArrancarPaginaRealAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(_registro);
        builder.Services.AddDataProtection();
        builder.Services.AddAntiforgery();
        builder.Services.AddRazorComponents();
        builder.Services.AddLocalization();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<AuthenticationStateProvider, EstadoDeLaPeticion>();
        builder.Services.AddSingleton<UserManager<ApplicationUser>>(_gestor);
        builder.Services.AddSingleton<SignInManager<ApplicationUser>>(_signIn);
        builder.Services.AddSingleton<MediatR.IMediator>(_mediador);
        builder.Services.AddSingleton<ISelloSeguridadCuenta>(_sellos);
        builder.Services.AddSingleton<CaeManager.Infrastructure.Coordinacion.IEleccionLiderService>(_cerrojo);
        builder.Services.AddSingleton<CaeManager.Application.Common.IClienteActivoSeleccionado>(new SeleccionEmpresaGestionadaDePrueba());
        var app = builder.Build();

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        // La cookie de una sesión recién iniciada con contraseña: cuenta, sello vigente, marca de inicio reciente
        // y talveg:sid, al que va ligada la prueba de reautenticación.
        app.MapGet("/entrar", async (HttpContext contexto) =>
        {
            var identidad = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, UsuarioId.ToString()),
                new Claim(new IdentityOptions().ClaimsIdentity.SecurityStampClaimType, SelloVigente),
                new Claim(SignInManagerCuentaDesactivada.ClaimAutenticacionSesion,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
                new Claim(SignInManagerCuentaDesactivada.ClaimSid, Guid.NewGuid().ToString("N")),
            ], CookieAuthenticationDefaults.AuthenticationScheme);
            await contexto.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identidad));
            return Results.Ok();
        });
        app.MapRazorComponents<RaizPaginaReal>()
            .AddAdditionalAssemblies(typeof(ConfigurarAutenticadorDosFactores).Assembly);
        await app.StartAsync();
        return app;
    }

    /// <summary>El estado que el renderizado estático toma del <see cref="HttpContext.User"/> de cada petición.</summary>
    private sealed class EstadoDeLaPeticion : AuthenticationStateProvider, IHostEnvironmentAuthenticationStateProvider
    {
        private Task<AuthenticationState>? _estado;

        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            _estado ?? throw new InvalidOperationException("El renderizado estático no fijó el estado de autenticación.");

        public void SetAuthenticationState(Task<AuthenticationState> authenticationStateTask) => _estado = authenticationStateTask;
    }

    private sealed class RaizPaginaReal : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder b)
        {
            b.OpenComponent<Router>(0);
            b.AddComponentParameter(1, nameof(Router.AppAssembly), typeof(ConfigurarAutenticadorDosFactores).Assembly);
            b.AddComponentParameter(2, nameof(Router.Found), (RenderFragment<RouteData>)(datos => c =>
            {
                c.OpenComponent<RouteView>(0);
                c.AddComponentParameter(1, nameof(RouteView.RouteData), datos);
                c.CloseComponent();
            }));
            b.CloseComponent();
        }
    }
}
