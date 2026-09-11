using Bunit;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Account;
using CaeManager.Web.Components.Account.Pages;
using CaeManager.Web.Components.Layout;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Opciones = Microsoft.Extensions.Options.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// Las pantallas de cuenta (2FA, recuperar, restablecer y cambiar contraseña) en
/// la escena de acceso del login, y los requisitos de contraseña que se marcan en
/// vivo. Lo que el navegador hace con acceso-contrasena.js no se ve aquí (bUnit
/// no ejecuta JS): aquí se prueba lo que el servidor pinta y que las reglas
/// anunciadas son las que Identity exige de verdad.
/// </summary>
public class AccesoCuentaEscenaTests : BunitContext
{
    private readonly AlmacenFalso _almacen = new();
    private readonly UserManager<ApplicationUser> _usuarios;

    public AccesoCuentaEscenaTests()
    {
        _usuarios = CrearUsuarios(_almacen, PoliticaDeLaApp());

        Services.AddSingleton(_usuarios);
        Services.AddSingleton(CrearSignIn(_usuarios));
        Services.AddSingleton<IOptions<IdentityOptions>>(Opciones.Create(PoliticaDeLaApp()));
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddSingleton<IEmailService>(new EmailQueNoDebeUsarse());
        AddAuthorization();
    }

    [Theory]
    [InlineData(typeof(LoginCon2fa))]
    [InlineData(typeof(OlvideContrasena))]
    [InlineData(typeof(RestablecerContrasena))]
    [InlineData(typeof(CambiarContrasena))]
    public void La_pantalla_se_monta_en_la_escena_de_acceso(Type pagina)
    {
        pagina.GetCustomAttributes(typeof(LayoutAttribute), inherit: true)
            .Cast<LayoutAttribute>().Should().ContainSingle()
            .Which.LayoutType.Should().Be(typeof(AccesoLayout));
    }

    /// <summary>
    /// El contrato de la lista: lo que se anuncia como pendiente es exactamente
    /// lo que el <see cref="PasswordValidator{TUser}"/> de Identity rechaza. Las
    /// muestras con «Á», «á» y «١» (dígito árabe) son las que separan ASCII de
    /// Unicode: con char.IsUpper la «Á» se anunciaba como mayúscula cumplida.
    /// </summary>
    [Theory]
    [InlineData("Abcdefghi1")]
    [InlineData("abcdefghi1")]
    [InlineData("ABCDEFGHI1")]
    [InlineData("Abcdefghij")]
    [InlineData("Abc1")]
    [InlineData("Ábcdefghi1")]
    [InlineData("áBCDEFGHI1")]
    [InlineData("Abcdefghi١")]
    [InlineData("Abcdefgh1!")]
    [InlineData("aaaaaaaaaaA1")]
    [InlineData("")]
    public async Task Los_requisitos_pendientes_son_los_que_rechaza_Identity(string contrasena)
    {
        var todas = new IdentityOptions();
        todas.Password.RequiredLength = 8;
        todas.Password.RequiredUniqueChars = 4;

        foreach (var opciones in new[] { PoliticaDeLaApp(), todas })
        {
            var usuarios = CrearUsuarios(new AlmacenFalso(), opciones);
            var resultado = await new PasswordValidator<ApplicationUser>()
                .ValidateAsync(usuarios, new ApplicationUser(), contrasena);
            // Con RequiredUniqueChars = 1 (el valor por defecto) Identity rechaza
            // la cadena vacía también por «distintos»; la lista no lo anuncia a
            // propósito: solo lo dispara la vacía, y esa ya incumple la longitud.
            var rechazadas = resultado.Errors.Select(e => ClavePorCodigo[e.Code])
                .Where(c => c != "distintos" || opciones.Password.RequiredUniqueChars > 1);

            ReglasContrasena.Evaluar(opciones.Password, contrasena)
                .Where(r => !r.Cumplido).Select(r => r.Clave)
                .Should().BeEquivalentTo(rechazadas,
                    $"«{contrasena}» con longitud {opciones.Password.RequiredLength}: la lista no puede prometer lo que el servidor rechaza");
        }
    }

    [Fact]
    public void La_lista_de_requisitos_sale_de_la_politica_y_marca_lo_cumplido()
    {
        var vacia = Render<RequisitosContrasena>(p => p
            .Add(r => r.IdCampo, "password-nueva")
            .Add(r => r.Politica, PoliticaDeLaApp().Password));

        var lista = vacia.Find("ul#password-nueva-requisitos");
        lista.GetAttribute("data-requisitos-de").Should().Be("password-nueva");
        var items = lista.QuerySelectorAll("li");
        items.Select(li => li.GetAttribute("data-regla"))
            .Should().Equal("longitud", "mayuscula", "minuscula", "numero");
        items[0].GetAttribute("data-minimo").Should().Be("10");
        items[1].HasAttribute("data-minimo").Should().BeFalse();
        items[0].TextContent.Should().Contain("Al menos 10 caracteres").And.Contain("pendiente");
        items.Should().OnlyContain(li => !li.ClassList.Contains("acceso-requisito-cumplido"));
        vacia.Find("[data-requisitos-resumen=password-nueva]").GetAttribute("aria-live").Should().Be("polite");

        var cumplida = Render<RequisitosContrasena>(p => p
            .Add(r => r.IdCampo, "password-nueva")
            .Add(r => r.Politica, PoliticaDeLaApp().Password)
            .Add(r => r.Valor, "Abcdefghi1"));

        cumplida.FindAll("li").Should().OnlyContain(li =>
            li.ClassList.Contains("acceso-requisito-cumplido") && li.TextContent.Contains("cumplido"));
    }

    [Fact]
    public void Restablecer_pinta_la_escena_los_requisitos_y_la_coincidencia_en_vivo()
    {
        var id = Guid.NewGuid();
        _almacen.Usuario = new ApplicationUser { Id = id, Email = "marta.ruiz@consultora.es" };
        var codigo = WebEncoders.Base64UrlEncode("token"u8.ToArray());
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"/cuenta/restablecer-contrasena?userId={id}&code={codigo}");

        var cut = Render<RestablecerContrasena>();

        cut.Find("h1 img").GetAttribute("alt").Should().Be("TALVEG");
        cut.Find("h2.acceso-titulo").TextContent.Should().Be("Nueva contraseña");
        cut.Find(".acceso-explica").TextContent.Should().Be("Para la cuenta marta.ruiz@consultora.es.");
        cut.Find("#password-nueva").GetAttribute("aria-describedby").Should().Be("password-nueva-requisitos");
        cut.FindAll("ul#password-nueva-requisitos li").Should().HaveCount(4);
        var coincidencia = cut.Find("[data-coincidencia-de=password-confirmar]");
        coincidencia.GetAttribute("data-coincidencia-con").Should().Be("password-nueva");
        coincidencia.GetAttribute("aria-live").Should().Be("polite");
        cut.FindAll("button[type=submit]").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Guardar y entrar");
    }

    [Fact]
    public void Cambiar_contrasena_anuncia_los_requisitos_de_la_nueva()
    {
        var cut = Render<CambiarContrasena>();

        cut.Find("h2.acceso-titulo").TextContent.Should().Be("Cambiar contraseña");
        cut.Find("#password-nueva").GetAttribute("aria-describedby").Should().Be("password-nueva-requisitos");
        cut.FindAll("ul#password-nueva-requisitos li").Should().HaveCount(4);
        cut.Find("[data-coincidencia-de=password-confirmar]").Should().NotBeNull();
        cut.FindAll("#password-actual").Should().ContainSingle("la actual no lleva requisitos: ya existe");
    }

    [Fact]
    public void La_verificacion_en_dos_pasos_tiene_titulo_y_salida_sin_estar_bloqueada()
    {
        Services.AddSingleton<SignInManager<ApplicationUser>>(new SignInManagerCon2faPendiente(_usuarios));

        var cut = Render<LoginCon2fa>();

        cut.Find("h2.acceso-titulo").TextContent.Should().Be("Verificación en dos pasos");
        cut.Find(".acceso-codigo #codigo").GetAttribute("autocomplete").Should().Be("one-time-code");
        cut.FindAll("button[type=submit]").Should().ContainSingle("Ayudas.IniciarSesionAsync pulsa el único envío tras rellenar #codigo");
        cut.Find(".acceso-olvido a").GetAttribute("href").Should().Be("/cuenta/iniciar-sesion");
    }

    /// <summary>
    /// La página es SSR estática: un botón con @onclick no tiene circuito que lo
    /// ejecute. El reenvío tiene que ser un envío de formulario que lleve el
    /// correo, o no hace nada fuera de bUnit (aquí sí se ejecutaría y daría verde).
    /// </summary>
    [Fact]
    public async Task Recuperar_contrasena_reenvia_con_un_formulario_y_no_con_un_clic_sin_circuito()
    {
        var cut = Render<OlvideContrasena>();
        cut.Find("h2.acceso-titulo").TextContent.Should().Be("¿Has olvidado la contraseña?");

        await cut.Find("#email").ChangeAsync(new ChangeEventArgs { Value = "nadie@consultora.es" });
        await cut.Find("form").SubmitAsync();

        cut.Find("h2.acceso-titulo").TextContent.Should().Be("Revisa tu correo");
        var reenvio = cut.Find("button.acceso-enlace-boton");
        reenvio.TextContent.Should().Be("vuelve a enviarlo");
        reenvio.GetAttribute("type").Should().Be("submit");
        reenvio.HasAttribute("blazor:onclick").Should().BeFalse("un @onclick no se ejecuta en una página estática");
        var formulario = reenvio.Closest("form");
        formulario.Should().NotBeNull();
        formulario!.QuerySelector("input[type=hidden][value='nadie@consultora.es']")
            .Should().NotBeNull("el segundo envío tiene que llevar el correo del primero");
    }

    private static readonly Dictionary<string, string> ClavePorCodigo = new()
    {
        ["PasswordTooShort"] = "longitud",
        ["PasswordRequiresUpper"] = "mayuscula",
        ["PasswordRequiresLower"] = "minuscula",
        ["PasswordRequiresDigit"] = "numero",
        ["PasswordRequiresNonAlphanumeric"] = "simbolo",
        ["PasswordRequiresUniqueChars"] = "distintos",
    };

    /// <summary>La de InfrastructureServiceCollectionExtensions: 10 caracteres, sin símbolo.</summary>
    private static IdentityOptions PoliticaDeLaApp()
    {
        var opciones = new IdentityOptions();
        opciones.Password.RequiredLength = 10;
        opciones.Password.RequireNonAlphanumeric = false;
        return opciones;
    }

    private static UserManager<ApplicationUser> CrearUsuarios(IUserStore<ApplicationUser> almacen, IdentityOptions opciones) => new(
        almacen, Opciones.Create(opciones), new PasswordHasher<ApplicationUser>(),
        [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
        NullLogger<UserManager<ApplicationUser>>.Instance);

    private static SignInManager<ApplicationUser> CrearSignIn(UserManager<ApplicationUser> usuarios) => new(
        usuarios, new HttpContextAccessor(),
        new UserClaimsPrincipalFactory<ApplicationUser>(usuarios, Opciones.Create(new IdentityOptions())),
        Opciones.Create(new IdentityOptions()), NullLogger<SignInManager<ApplicationUser>>.Instance,
        new AuthenticationSchemeProvider(Opciones.Create(new AuthenticationOptions())),
        new DefaultUserConfirmation<ApplicationUser>());

    private sealed class SignInManagerCon2faPendiente(UserManager<ApplicationUser> usuarios) : SignInManager<ApplicationUser>(
        usuarios, new HttpContextAccessor(),
        new UserClaimsPrincipalFactory<ApplicationUser>(usuarios, Opciones.Create(new IdentityOptions())),
        Opciones.Create(new IdentityOptions()), NullLogger<SignInManager<ApplicationUser>>.Instance,
        new AuthenticationSchemeProvider(Opciones.Create(new AuthenticationOptions())),
        new DefaultUserConfirmation<ApplicationUser>())
    {
        public override Task<ApplicationUser?> GetTwoFactorAuthenticationUserAsync() =>
            Task.FromResult<ApplicationUser?>(new ApplicationUser { Id = Guid.NewGuid() });
    }

    private sealed class EmailQueNoDebeUsarse : IEmailService
    {
        public Task<Result> EnviarAsync(string destinatarioEmail, string asunto, string cuerpoHtml, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("con una cuenta que no existe no se envía ningún correo");
    }

    /// <summary>Solo lo que las páginas consultan: buscar por id y por correo.</summary>
    private sealed class AlmacenFalso : IUserStore<ApplicationUser>, IUserEmailStore<ApplicationUser>
    {
        public ApplicationUser? Usuario { get; set; }

        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken ct) =>
            Task.FromResult(Usuario is not null && Usuario.Id.ToString() == userId ? Usuario : null);

        public Task<ApplicationUser?> FindByEmailAsync(string normalizedEmail, CancellationToken ct) =>
            Task.FromResult<ApplicationUser?>(null);

        public void Dispose() { }
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.Id.ToString());
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct) => throw new NotSupportedException();
        public Task SetEmailAsync(ApplicationUser user, string? email, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetEmailAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.Email);
        public Task<bool> GetEmailConfirmedAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task SetEmailConfirmedAsync(ApplicationUser user, bool confirmed, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetNormalizedEmailAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task SetNormalizedEmailAsync(ApplicationUser user, string? normalizedEmail, CancellationToken ct) => throw new NotSupportedException();
    }
}
