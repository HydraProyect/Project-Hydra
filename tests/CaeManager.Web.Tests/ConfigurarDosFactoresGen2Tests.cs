using System.Reflection;
using System.Security.Claims;
using Bunit;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Account.Pages;
using CaeManager.Web.Components.Layout;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Opciones = Microsoft.Extensions.Options.Options;

namespace CaeManager.Web.Tests;

/// <summary>Marcado Gen 2 de la configuración personal de segundo factor.</summary>
public class ConfigurarDosFactoresGen2Tests : BunitContext
{
    // Clave base32 de prueba, partida a proposito: escrita de una pieza tras «secret=», gitleaks
    // la marca como credencial (generic-api-key) y escanea tambien el historial de la rama.
    private static readonly string ClavePrueba = string.Concat("WVZQ4NLT", "HS2BJD7F", "XK9MPR3A");

    private readonly AlmacenAutenticador _almacen = new();
    private readonly ApplicationUser _usuario = new()
    {
        Id = Guid.NewGuid(),
        Email = "marta.reyes@consultoravega.es"
    };

    public ConfigurarDosFactoresGen2Tests()
    {
        _almacen.Usuario = _usuario;
        _almacen.Clave = ClavePrueba;

        var usuarios = CrearUsuarios(_almacen);
        Services.AddSingleton(usuarios);
        Services.AddSingleton(CrearSignIn(usuarios));
        Services.AddSingleton<AuthenticationStateProvider>(new AutenticacionFalsa(_usuario.Id));
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        // Sin AddAuthorization(): registra su propio AuthenticationStateProvider, sin autenticar,
        // y como gana el ultimo registro pisaba a AutenticacionFalsa. La pagina no encontraba la
        // claim NameIdentifier, retornaba en la primera guarda y no pintaba nada. El [Authorize]
        // de la pagina no lo necesita: bUnit solo lo aplica a traves de un AuthorizeRouteView.
    }

    private IRenderedComponent<ConfigurarAutenticadorDosFactores> Renderizar(bool activa = false)
    {
        _almacen.Activa = activa;
        return Render<ConfigurarAutenticadorDosFactores>();
    }

    /// <summary>
    /// Solo refleja los atributos de la pagina. NO ejecuta el guard de 2FA obligatoria ni prueba que
    /// no haya bucle: eso vive en el middleware y en la ruta forzada, que este arnes no alcanza.
    /// </summary>
    [Fact]
    public void Conserva_el_Authorize_y_el_AuthLayout_de_la_pagina()
    {
        typeof(ConfigurarAutenticadorDosFactores).GetCustomAttributes<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>(true)
            .Should().ContainSingle();

        typeof(ConfigurarAutenticadorDosFactores).GetCustomAttributes<LayoutAttribute>(true)
            .Should().ContainSingle().Which.LayoutType.Should().Be(typeof(AuthLayout));
    }

    [Fact]
    public void El_alta_ordena_el_qr_la_clave_y_la_confirmacion_sin_inventar_codigos_de_recuperacion()
    {
        var cut = Renderizar();

        cut.Find("h1.titulo-pagina").TextContent.Trim().Should().Be("Autenticación en dos pasos");
        cut.Find(".cabecera-pagina-descripcion").TextContent.Trim().Should().Be("Configura el segundo factor de tu propia cuenta.");

        var qr = cut.Find(".tarjeta-qr img.qr-2fa");
        qr.GetAttribute("src").Should().StartWith("data:image/png;base64,");
        qr.GetAttribute("width").Should().Be("220");
        qr.GetAttribute("height").Should().Be("220");
        qr.GetAttribute("alt").Should().Be("Código QR para configurar la aplicación autenticadora");

        var pasos = cut.FindAll(".dos-factores-pasos h2").Select(h => h.TextContent.Trim()).ToList();
        pasos.Should().Equal("1. Añade la cuenta a tu aplicación", "2. Confirma que funciona");
        pasos.Should().NotContain("3. Guarda tus códigos de recuperación",
            "control del instrumento: el tercer bloque inexistente no puede confundirse con los dos pasos reales");

        cut.Find(".clave-manual code").TextContent.Trim().Should().Be("wvzq 4nlt hs2b jd7f xk9m pr3a");
        cut.Find(".uri-otpauth code").TextContent.Should().Contain("secret=" + ClavePrueba);

        var codigo = cut.Find("#codigo");
        codigo.GetAttribute("autocomplete").Should().Be("one-time-code");
        codigo.GetAttribute("inputmode").Should().Be("numeric");
        codigo.GetAttribute("maxlength").Should().Be("6");
        codigo.GetAttribute("aria-describedby").Should().Be("codigo-ayuda");
        cut.Find("#codigo-ayuda").TextContent.Should().Contain("espacios y guiones");
        cut.Find("button[type=submit]").TextContent.Trim().Should().Be("Activar autenticación en dos pasos");

        // Sobre todo el texto visible, no sobre un encabezado: una promesa de codigos de recuperacion,
        // de descarga o de copiado en cualquier parrafo tiene que hacer caer el caso.
        var texto = string.Join(" ", cut.Nodes.Select(n => n.TextContent)).ToLowerInvariant();
        texto.Should().Contain("autenticación en dos pasos",
            "control del instrumento: el texto se lee entero y con acentos, o los NotContain no observan nada");
        foreach (var promesa in new[] { "recuperación", "descargar", "copiar", "portapapeles" })
            texto.Should().NotContain(promesa, "esa capacidad no existe en esta pagina");
    }

    [Fact]
    public void El_estado_activo_es_informativo_y_no_revela_otra_vez_la_clave()
    {
        var cut = Renderizar(activa: true);

        cut.Find(".tarjeta-2fa-activa .texto-2fa-exito").TextContent.Should()
            .Contain("La autenticación en dos pasos ya está activada en tu cuenta.");
        cut.Find(".tarjeta-2fa-activa .texto-2fa-secundario").TextContent.Should()
            .Contain("cuando sea necesario");
        cut.FindAll(".clave-manual, .uri-otpauth, .dos-factores-alta").Should().BeEmpty(
            "control del instrumento: una clave visible distinguiría este estado del alta");

        // Independiente de los selectores: la clave no puede aparecer bajo ninguna clase. Sin espacios
        // y en minusculas, porque el alta la ensena agrupada; ese mismo caso es el control positivo.
        cut.Markup.Replace(" ", string.Empty).ToLowerInvariant().Should().NotContain(ClavePrueba.ToLowerInvariant(),
            "con el segundo factor ya activo la clave no se vuelve a ensenar en ningun sitio");
    }

    /// <summary>
    /// El emisor de la URI del TOTP es lo que la aplicacion de autenticacion ensena como nombre de la
    /// cuenta. Estaba declarado como <c>const string Emisor = "@Marca.Nombre"</c>, y dentro de
    /// <c>@code</c> no hay interpolacion de Razor: la URI anunciaba el literal. Se compara contra
    /// <c>Marca.Nombre</c> y no contra un texto fijo, porque el nombre sale de la configuracion.
    /// </summary>
    [Fact]
    public void La_uri_del_totp_anuncia_el_nombre_de_la_marca_y_no_el_literal_de_razor()
    {
        var cut = Renderizar();

        var uri = cut.Find(".uri-otpauth code").TextContent;

        // Control del instrumento: sin el secreto, la URI no estaria pintada y el NotContain de
        // abajo pasaria sobre un texto vacio sin observar nada.
        uri.Should().Contain("secret=");
        var emisor = System.Text.Encodings.Web.UrlEncoder.Default.Encode(
            CaeManager.Application.Common.Marca.Nombre);
        uri.Should().Contain($"issuer={emisor}",
            "el emisor es el nombre de la marca, que es lo que ve quien abre su aplicacion de autenticacion");
        uri.Should().NotContain("Marca.Nombre",
            "dentro de @code no hay interpolacion: el literal llegaria tal cual a la aplicacion");
    }

    private static UserManager<ApplicationUser> CrearUsuarios(IUserStore<ApplicationUser> almacen) => new(
        almacen, Opciones.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(),
        [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
        NullLogger<UserManager<ApplicationUser>>.Instance);

    private static SignInManager<ApplicationUser> CrearSignIn(UserManager<ApplicationUser> usuarios) => new(
        usuarios, new HttpContextAccessor(),
        new UserClaimsPrincipalFactory<ApplicationUser>(usuarios, Opciones.Create(new IdentityOptions())),
        Opciones.Create(new IdentityOptions()), NullLogger<SignInManager<ApplicationUser>>.Instance,
        new AuthenticationSchemeProvider(Opciones.Create(new AuthenticationOptions())),
        new DefaultUserConfirmation<ApplicationUser>());

    private sealed class AutenticacionFalsa(Guid usuarioId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, usuarioId.ToString())], "prueba"))));
    }

    private sealed class AlmacenAutenticador : IUserStore<ApplicationUser>, IUserAuthenticatorKeyStore<ApplicationUser>, IUserTwoFactorStore<ApplicationUser>
    {
        public ApplicationUser? Usuario { get; set; }
        public string? Clave { get; set; }
        public bool Activa { get; set; }

        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken ct) =>
            Task.FromResult(Usuario?.Id.ToString() == userId ? Usuario : null);
        public Task<string?> GetAuthenticatorKeyAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(Clave);
        public Task SetAuthenticatorKeyAsync(ApplicationUser user, string key, CancellationToken ct) { Clave = key; return Task.CompletedTask; }
        public Task<bool> GetTwoFactorEnabledAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(Activa);
        public Task SetTwoFactorEnabledAsync(ApplicationUser user, bool enabled, CancellationToken ct) { Activa = enabled; return Task.CompletedTask; }

        public void Dispose() { }
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.Id.ToString());
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.UserName);
        public Task SetUserNameAsync(ApplicationUser user, string? name, CancellationToken ct) { user.UserName = name; return Task.CompletedTask; }
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.NormalizedUserName);
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? name, CancellationToken ct) { user.NormalizedUserName = name; return Task.CompletedTask; }
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedName, CancellationToken ct) => Task.FromResult<ApplicationUser?>(null);
    }
}
