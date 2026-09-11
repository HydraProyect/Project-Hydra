using Bunit;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Account.Pages;
using CaeManager.Web.Components.Layout;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Opciones = Microsoft.Extensions.Options.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// La pantalla de acceso contra el artefacto aprobado «Luz de agua»: lo que el
/// artefacto fija (lockup, lema, «Entrar», escena con interruptor de movimiento)
/// y lo que el código ya hacía y el artefacto no pinta (SSO condicional, sus
/// errores, el enlace de olvido). Los selectores #email, #password y el único
/// botón de envío son los que usa Ayudas.IniciarSesionAsync en todos los E2E.
/// </summary>
public class AccesoLuzDeAguaTests : BunitContext
{
    private readonly AzureAdOptions _azureAd = new();

    public AccesoLuzDeAguaTests()
    {
        var usuarios = new UserManager<ApplicationUser>(
            new AlmacenSinUso(), Opciones.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(),
            [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);
        var signIn = new SignInManager<ApplicationUser>(
            usuarios, new HttpContextAccessor(),
            new UserClaimsPrincipalFactory<ApplicationUser>(usuarios, Opciones.Create(new IdentityOptions())),
            Opciones.Create(new IdentityOptions()), NullLogger<SignInManager<ApplicationUser>>.Instance,
            new AuthenticationSchemeProvider(Opciones.Create(new AuthenticationOptions())),
            new DefaultUserConfirmation<ApplicationUser>());

        Services.AddSingleton(usuarios);
        Services.AddSingleton(signIn);
        Services.AddSingleton<IOptions<AzureAdOptions>>(Opciones.Create(_azureAd));
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
    }

    [Fact]
    public void Pinta_el_lockup_como_titulo_el_lema_y_el_formulario_que_usan_los_E2E()
    {
        var cut = Render<Login>();

        var titulo = cut.Find("h1");
        var lockup = titulo.QuerySelector("img")!;
        lockup.GetAttribute("src").Should().Be("img/marca/talveg-lockup-oscuro.svg");
        lockup.GetAttribute("alt").Should().Be("TALVEG", "el nombre accesible del título es la palabra que dibuja el lockup");

        cut.Find(".acceso-lema").TextContent.Should().Be("Coordinación de actividades empresariales");
        cut.Find("label[for=email]").TextContent.Should().Be("Correo electrónico");
        cut.Find("label[for=password]").TextContent.Should().Be("Contraseña");
        cut.Find("#email").GetAttribute("autocomplete").Should().Be("username");
        cut.Find("#email").GetAttribute("type").Should().Be("email", "teclado de correo en móvil");
        cut.Find("form").HasAttribute("novalidate").Should().BeTrue(
            "con type=email y sin novalidate la burbuja nativa taparía los mensajes del servidor");
        cut.Find("#password").GetAttribute("type").Should().Be("password");

        var envios = cut.FindAll("button[type=submit]");
        envios.Should().ContainSingle("Ayudas.IniciarSesionAsync pulsa el único botón de envío");
        envios[0].TextContent.Should().Be("Entrar");

        var olvido = cut.Find(".acceso-olvido a");
        olvido.TextContent.Should().Be("¿Has olvidado la contraseña?");
        olvido.GetAttribute("href").Should().Be("/cuenta/olvide-contrasena");
    }

    /// <summary>
    /// Los demás tests renderizan Login y AccesoLayout por separado: sin este,
    /// volver a AuthLayout dejaría todo en verde con la pantalla sin escena.
    /// </summary>
    [Fact]
    public void Login_se_monta_en_la_escena_de_acceso()
    {
        typeof(Login).GetCustomAttributes(typeof(LayoutAttribute), inherit: true)
            .Cast<LayoutAttribute>().Should().ContainSingle()
            .Which.LayoutType.Should().Be(typeof(AccesoLayout));
    }

    [Fact]
    public void Sin_SSO_configurado_no_ofrece_Microsoft_ni_avisa_de_acceso_de_consulta()
    {
        var cut = Render<Login>();

        cut.FindAll("a[href^='/cuenta/iniciar-sesion-microsoft']").Should().BeEmpty();
        cut.Markup.Should().NotContain("Microsoft");
        cut.Markup.Should().NotContain("solo de consulta");
    }

    [Fact]
    public void Con_SSO_configurado_ofrece_continuar_con_Microsoft_conservando_el_destino_y_avisa_de_la_consulta()
    {
        ConfigurarSso();
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/cuenta/iniciar-sesion?ReturnUrl=%2Fdocumentos%3Fvista%3Dmia");

        var cut = Render<Login>();

        var enlace = cut.Find(".acceso-alterno a");
        enlace.TextContent.Should().Be("continuar con Microsoft");
        enlace.GetAttribute("href").Should().Be("/cuenta/iniciar-sesion-microsoft?returnUrl=%2Fdocumentos%3Fvista%3Dmia");
        cut.Find(".acceso-nota").TextContent.Should().Be("Con correo y contraseña, el acceso es solo de consulta.");
    }

    [Fact]
    public void El_error_de_SSO_sin_cuenta_nombra_la_marca_y_no_el_literal_de_Razor()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/cuenta/iniciar-sesion?ErrorSso=sin-cuenta");

        var cut = Render<Login>();

        var alerta = cut.Find("[role=alert]");
        alerta.TextContent.Should().Be(
            $"Tu cuenta de Microsoft no está dada de alta en {Marca.Nombre}. Contacta con tu administrador.");
        alerta.TextContent.Should().NotContain("@", "una cadena C# no evalúa Razor: el nombre tiene que interpolarse");
    }

    [Fact]
    public void El_error_de_SSO_fallido_se_dice_como_alerta()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/cuenta/iniciar-sesion?ErrorSso=fallo");

        var cut = Render<Login>();

        cut.Find("[role=alert]").TextContent.Should()
            .Be("No pudimos completar el inicio de sesión con Microsoft. Intenta nuevamente.");
    }

    [Fact]
    public void La_escena_deja_el_lienzo_fuera_del_arbol_accesible_y_el_interruptor_oculto_hasta_que_haya_guion()
    {
        var cut = Render<AccesoLayout>(p => p.Add(l => l.Body, (RenderFragment)(b => b.AddMarkupContent(0, "<p id=cuerpo>cuerpo</p>"))));

        cut.Find("canvas[data-acceso-agua]").GetAttribute("aria-hidden").Should().Be("true");
        cut.Find("main #cuerpo").Should().NotBeNull("el contenido de la página va en el punto de referencia principal");

        var interruptor = cut.Find("button[data-acceso-mov]");
        interruptor.GetAttribute("type").Should().Be("button", "dentro o fuera de un formulario, no puede enviarlo");
        interruptor.HasAttribute("hidden").Should().BeTrue("sin acceso-agua.js el botón no haría nada");
        interruptor.GetAttribute("aria-pressed").Should().Be("true");
        interruptor.QuerySelector("[data-acceso-mov-texto]")!.TextContent.Should().Be("Desactivar el movimiento del fondo");

        var aviso = cut.Find("[data-acceso-aviso]");
        aviso.GetAttribute("role").Should().Be("status");
        aviso.GetAttribute("aria-live").Should().Be("polite");
    }

    private void ConfigurarSso()
    {
        _azureAd.TenantId = "tenant";
        _azureAd.ClientId = "cliente-app";
        _azureAd.ClientSecret = "secreto";
    }

    /// <summary>El UserManager solo existe para construir el SignInManager; no se consulta.</summary>
    private sealed class AlmacenSinUso : IUserStore<ApplicationUser>
    {
        public void Dispose() { }
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct) => throw new NotSupportedException();
    }
}
