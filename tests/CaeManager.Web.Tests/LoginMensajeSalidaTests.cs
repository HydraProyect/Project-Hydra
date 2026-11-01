using Bunit;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Account.Pages;
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
/// El error del login es un único texto para credenciales inválidas y cuenta
/// bloqueada o desactivada (simetría contra la enumeración de cuentas, ASVS 4.0.3
/// V2.2, OWASP A07), y ofrece una salida: restablecer la contraseña o consultar con
/// quien gestiona el acceso en la organización. El texto no revela cuál de los dos
/// casos ocurrió y no nombra ningún buzón de soporte que aún no existe.
/// </summary>
public class LoginMensajeSalidaTests : BunitContext
{
    private readonly SignInManagerFalso _signIn = new();

    public LoginMensajeSalidaTests()
    {
        Services.AddSingleton<UserManager<ApplicationUser>>(_signIn.Usuarios);
        Services.AddSingleton<SignInManager<ApplicationUser>>(_signIn);
        Services.AddSingleton<IOptions<AzureAdOptions>>(Opciones.Create(new AzureAdOptions()));
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddLocalization();
    }

    [Fact]
    public async Task Credenciales_invalidas_y_cuenta_bloqueada_dan_el_mismo_texto_con_la_salida()
    {
        _signIn.Resultado = SignInResult.Failed;
        var invalidas = await EnviarYLeerAlertaAsync();

        _signIn.Resultado = SignInResult.LockedOut;
        var bloqueada = await EnviarYLeerAlertaAsync();

        bloqueada.Should().Be(invalidas, "un texto distinto dejaría enumerar qué cuentas existen y están bloqueadas");
        invalidas.Should().Contain("restablece tu contraseña");
        invalidas.Should().Contain("quien gestiona tu acceso");
        invalidas.Should().NotContain("soporte@", "el buzón de soporte aún no existe");
    }

    private async Task<string> EnviarYLeerAlertaAsync()
    {
        var cut = Render<Login>();
        await cut.Find("#email").ChangeAsync(new ChangeEventArgs { Value = "alguien@example.com" });
        await cut.Find("#password").ChangeAsync(new ChangeEventArgs { Value = "una-contrasena" });
        await cut.Find("form").SubmitAsync();

        return cut.Find("[role=alert]").TextContent;
    }

    /// <summary>Solo se sustituye la comprobación de contraseña, que es lo que usa la página.</summary>
    private sealed class SignInManagerFalso : SignInManager<ApplicationUser>
    {
        public SignInManagerFalso() : this(new UsuariosSinCuenta())
        {
        }

        private SignInManagerFalso(UsuariosSinCuenta usuarios)
            : base(usuarios, new HttpContextAccessor(),
                new UserClaimsPrincipalFactory<ApplicationUser>(usuarios, Opciones.Create(new IdentityOptions())),
                Opciones.Create(new IdentityOptions()), NullLogger<SignInManager<ApplicationUser>>.Instance,
                new AuthenticationSchemeProvider(Opciones.Create(new AuthenticationOptions())),
                new DefaultUserConfirmation<ApplicationUser>())
        {
            Usuarios = usuarios;
        }

        public UserManager<ApplicationUser> Usuarios { get; }

        public SignInResult Resultado { get; set; } = SignInResult.Failed;

        public override Task<SignInResult> PasswordSignInAsync(
            string userName, string password, bool isPersistent, bool lockoutOnFailure)
            => Task.FromResult(Resultado);
    }

    /// <summary>El almacén del arnés lanza NotSupportedException: la búsqueda por correo se sustituye.</summary>
    private sealed class UsuariosSinCuenta() : UserManager<ApplicationUser>(
        new AlmacenSinUso(), Opciones.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(),
        [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
        NullLogger<UserManager<ApplicationUser>>.Instance)
    {
        public override Task<ApplicationUser?> FindByEmailAsync(string email) => Task.FromResult<ApplicationUser?>(null);
    }

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
