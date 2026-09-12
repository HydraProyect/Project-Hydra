using System.Security.Claims;
using Bunit;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Account;
using CaeManager.Web.Components.Account.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Opciones = Microsoft.Extensions.Options.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// Las dos escrituras de Identity que ConfigurarAutenticadorDosFactores.razor
/// ignoraba: <c>ResetAuthenticatorKeyAsync</c> al cargar la pantalla y
/// <c>SetTwoFactorEnabledAsync</c> al activar. El almacén falso puede fallar a
/// voluntad en su <c>UpdateAsync</c> —el paso real donde Identity confirma
/// cualquiera de las dos escrituras—, igual que <c>AlmacenFalso</c> en
/// <see cref="AccesoCuentaEscenaTests"/>: aquí hace falta, además, que sepa de
/// clave de autenticador y de segundo factor.
/// </summary>
public class ConfigurarAutenticadorDosFactoresEscrituraTests : BunitContext
{
    private static readonly Guid UsuarioId = Guid.NewGuid();

    private readonly AlmacenAutenticadorFalso _almacen = new()
    {
        Usuario = new ApplicationUser { Id = UsuarioId, Email = "marta@arcospa.es", UserName = "marta@arcospa.es" },
    };

    private readonly SignInManagerFalso _signIn = new();
    private readonly RegistroCapturado _registro = new();

    public ConfigurarAutenticadorDosFactoresEscrituraTests()
    {
        Services.AddSingleton<UserManager<ApplicationUser>>(new GestorUsuariosFallaAVoluntad(_almacen));
        Services.AddSingleton<SignInManager<ApplicationUser>>(_signIn);
        Services.AddSingleton<ILoggerFactory>(new LoggerFactory([_registro]));
        Services.AddSingleton<AuthenticationStateProvider>(new AutenticacionFalsa(UsuarioId));
    }

    [Fact]
    public void Si_ResetAuthenticatorKeyAsync_falla_no_enseña_un_QR_que_el_servidor_no_guardo()
    {
        _almacen.FallaAlActualizar = true;
        _almacen.MensajeError = "El almacén no está disponible.";

        var cut = Render<ConfigurarAutenticadorDosFactores>();

        cut.Find("[role=alert]").TextContent.Should().Contain("El almacén no está disponible.");
        cut.FindAll("img.qr-2fa").Should().BeEmpty("la clave nunca quedó guardada: no hay QR que enseñar");
        cut.FindAll(".clave-manual").Should().BeEmpty("tampoco una clave manual que registrar a mano, por la misma razón");
        cut.FindAll("form").Should().BeEmpty("sin clave guardada no hay nada todavía que activar");
    }

    /// <summary>Control: sin fallo, la pantalla de siempre.</summary>
    [Fact]
    public void Si_ResetAuthenticatorKeyAsync_funciona_enseña_el_QR_de_verdad()
    {
        var cut = Render<ConfigurarAutenticadorDosFactores>();

        cut.FindAll("img.qr-2fa").Should().ContainSingle();
        cut.FindAll("[role=alert]").Should().BeEmpty();
    }

    [Fact]
    public async Task Si_SetTwoFactorEnabledAsync_falla_la_cuenta_sigue_sin_segundo_factor()
    {
        var cut = Render<ConfigurarAutenticadorDosFactores>();
        var uriAntes = Services.GetRequiredService<NavigationManager>().Uri;
        _almacen.FallaAlActualizar = true;
        _almacen.MensajeError = "No se pudo actualizar la cuenta.";

        await EnviarCodigoAsync(cut);

        cut.Find("[role=alert]").TextContent.Should().Contain("No se pudo actualizar la cuenta.");
        _almacen.DosFactoresActivo.Should().BeFalse("la escritura falló: Identity nunca llegó a activarlo de verdad");
        _signIn.SesionReemitida.Should().BeFalse("no se reemite una sesión sobre una activación que no ocurrió");
        _registro.Mensajes.Should().BeEmpty("no se audita una activación que no pasó");
        Services.GetRequiredService<NavigationManager>().Uri.Should().Be(uriAntes, "un fallo no navega como si hubiera terminado");
    }

    [Fact]
    public async Task Si_SetTwoFactorEnabledAsync_funciona_activa_reemite_sesion_y_audita()
    {
        var cut = Render<ConfigurarAutenticadorDosFactores>();

        await EnviarCodigoAsync(cut);

        _almacen.DosFactoresActivo.Should().BeTrue();
        _signIn.SesionReemitida.Should().BeTrue();
        _registro.Mensajes.Should().ContainSingle(m => m.Contains("2FA activado"));
    }

    private static async Task EnviarCodigoAsync(IRenderedComponent<ConfigurarAutenticadorDosFactores> cut)
    {
        await cut.Find("#codigo").ChangeAsync(new ChangeEventArgs { Value = GestorUsuariosFallaAVoluntad.CodigoValido });
        await cut.Find("form").SubmitAsync();
    }

    private sealed class AutenticacionFalsa(Guid usuarioId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, usuarioId.ToString())], "prueba"))));
    }

    /// <summary>
    /// <see cref="UserManager{TUser}"/> es una clase concreta; solo se sustituye
    /// el método que exige generar un TOTP real —lo que se prueba aquí es cómo
    /// reacciona la página al <see cref="IdentityResult"/> de la escritura, no
    /// el algoritmo TOTP en sí—. Reset/Set/Get siguen siendo los de verdad,
    /// corriendo contra <see cref="AlmacenAutenticadorFalso"/>.
    /// </summary>
    private sealed class GestorUsuariosFallaAVoluntad(IUserStore<ApplicationUser> almacen) : UserManager<ApplicationUser>(
        almacen, Opciones.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(),
        [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
        NullLogger<UserManager<ApplicationUser>>.Instance)
    {
        public const string CodigoValido = "123456";

        public override Task<bool> VerifyTwoFactorTokenAsync(ApplicationUser user, string tokenProvider, string token) =>
            Task.FromResult(token == CodigoValido);
    }

    /// <summary>
    /// Solo existe para que <see cref="SignInManagerFalso"/> tenga un
    /// <see cref="UserManager{TUser}"/> con el que construirse; la página no lo
    /// consulta a través de este SignInManager.
    /// </summary>
    private sealed class AlmacenSinUsoParaSignIn : IUserStore<ApplicationUser>
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

    /// <summary>
    /// <see cref="SignInManager{TUser}"/> es concreta; solo se sustituye
    /// <c>RefreshSignInAsync</c>, que en producción necesita un
    /// <see cref="HttpContext"/> de verdad con el que reemitir la cookie —aquí
    /// solo hace falta saber si se llamó.
    /// </summary>
    private sealed class SignInManagerFalso : SignInManager<ApplicationUser>
    {
        private static readonly UserManager<ApplicationUser> UsuariosParaConstruir = new(
            new AlmacenSinUsoParaSignIn(), Opciones.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(),
            [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);

        public SignInManagerFalso()
            : base(UsuariosParaConstruir, new HttpContextAccessor(),
                new UserClaimsPrincipalFactory<ApplicationUser>(UsuariosParaConstruir, Opciones.Create(new IdentityOptions())),
                Opciones.Create(new IdentityOptions()), NullLogger<SignInManager<ApplicationUser>>.Instance,
                new AuthenticationSchemeProvider(Opciones.Create(new AuthenticationOptions())),
                new DefaultUserConfirmation<ApplicationUser>())
        {
        }

        public bool SesionReemitida { get; private set; }

        public override Task RefreshSignInAsync(ApplicationUser user)
        {
            SesionReemitida = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>Recoge lo que se escribe en la categoría de auditoría de autenticación.</summary>
    private sealed class RegistroCapturado : ILoggerProvider
    {
        public List<string> Mensajes { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Registro(this, categoryName);

        public void Dispose() { }

        private sealed class Registro(RegistroCapturado dueno, string categoria) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (categoria == AuditoriaAutenticacion.CategoriaLog)
                    dueno.Mensajes.Add(formatter(state, exception));
            }
        }
    }

    /// <summary>
    /// Solo lo que <c>ResetAuthenticatorKeyAsync</c> y <c>SetTwoFactorEnabledAsync</c>
    /// tocan de verdad. El fallo se controla en <c>UpdateAsync</c> porque ahí es
    /// donde Identity confirma cualquiera de las dos escrituras — igual da cuál
    /// de ellas lo llame, cualquiera puede fallar a voluntad con
    /// <see cref="FallaAlActualizar"/>.
    /// </summary>
    private sealed class AlmacenAutenticadorFalso :
        IUserStore<ApplicationUser>,
        IUserEmailStore<ApplicationUser>,
        IUserAuthenticatorKeyStore<ApplicationUser>,
        IUserTwoFactorStore<ApplicationUser>
    {
        // Set...Async es el paso que Identity hace SIEMPRE, sobre la entidad en
        // memoria, antes de intentar guardar — igual que un DbContext rastrea un
        // cambio antes de SaveChangesAsync. Solo UpdateAsync (el guardado real)
        // decide si ese cambio pendiente se confirma o se descarta: si
        // FallaAlActualizar, el pendiente se tira y lo que se observa desde
        // fuera (DosFactoresActivo, la clave) no se mueve un ápice.
        private string? _claveAutenticadorPendiente;
        private string? _claveAutenticador;
        private bool _dosFactoresPendiente;

        public ApplicationUser? Usuario { get; set; }
        public bool FallaAlActualizar { get; set; }
        public string? MensajeError { get; set; }
        public bool DosFactoresActivo { get; private set; }

        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken ct) =>
            Task.FromResult(Usuario is not null && Usuario.Id.ToString() == userId ? Usuario : null);

        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct) =>
            Task.FromResult<ApplicationUser?>(null);

        public Task<ApplicationUser?> FindByEmailAsync(string normalizedEmail, CancellationToken ct) =>
            Task.FromResult<ApplicationUser?>(null);

        public void Dispose() { }
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.Id.ToString());
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.UserName);

        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken ct)
        {
            user.UserName = userName;
            return Task.CompletedTask;
        }

        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.NormalizedUserName);

        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken ct)
        {
            user.NormalizedUserName = normalizedName;
            return Task.CompletedTask;
        }

        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();

        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken ct)
        {
            if (FallaAlActualizar)
            {
                return Task.FromResult(IdentityResult.Failed(
                    new IdentityError { Code = "FalloDePrueba", Description = MensajeError ?? "No se pudo actualizar la cuenta." }));
            }

            if (_claveAutenticadorPendiente is not null)
                _claveAutenticador = _claveAutenticadorPendiente;
            DosFactoresActivo = _dosFactoresPendiente;

            return Task.FromResult(IdentityResult.Success);
        }

        public Task SetEmailAsync(ApplicationUser user, string? email, CancellationToken ct)
        {
            user.Email = email;
            return Task.CompletedTask;
        }

        public Task<string?> GetEmailAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.Email);
        public Task<bool> GetEmailConfirmedAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(true);
        public Task SetEmailConfirmedAsync(ApplicationUser user, bool confirmed, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetNormalizedEmailAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.Email?.ToUpperInvariant());
        public Task SetNormalizedEmailAsync(ApplicationUser user, string? normalizedEmail, CancellationToken ct) => Task.CompletedTask;

        public Task SetAuthenticatorKeyAsync(ApplicationUser user, string key, CancellationToken ct)
        {
            _claveAutenticadorPendiente = key;
            return Task.CompletedTask;
        }

        public Task<string?> GetAuthenticatorKeyAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(_claveAutenticador);

        public Task SetTwoFactorEnabledAsync(ApplicationUser user, bool enabled, CancellationToken ct)
        {
            _dosFactoresPendiente = enabled;
            return Task.CompletedTask;
        }

        public Task<bool> GetTwoFactorEnabledAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(DosFactoresActivo);
    }
}
