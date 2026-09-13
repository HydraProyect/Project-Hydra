using Bunit;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Account;
using CaeManager.Web.Components.Account.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
// Dentro de la subclase de SignInManager, «Options» es la propiedad heredada.
using Opciones = Microsoft.Extensions.Options.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// En el paso de verificación en dos pasos, una cuenta bloqueada no puede
/// recibir «Código no válido. Revisa tu aplicación autenticadora e intenta
/// nuevamente.»: la manda a reintentar algo que no funcionará mientras dure el
/// bloqueo. Al revés que en Login.razor, aquí el bloqueo sí se dice, porque
/// para llegar a este paso la contraseña ya está demostrada y no hay cuentas
/// que enumerar.
/// </summary>
public class VerificacionDosPasosBloqueoTests : BunitContext
{
    private const string TextoCodigoNoValido = "Código no válido";
    private const string TextoBloqueo = "Tu cuenta está bloqueada temporalmente";

    private readonly SignInManagerFalso _signIn = new();
    private readonly RegistroCapturado _registro = new();

    public VerificacionDosPasosBloqueoTests()
    {
        Services.AddSingleton<SignInManager<ApplicationUser>>(_signIn);
        Services.AddSingleton<ILoggerFactory>(new LoggerFactory([_registro]));
        Services.AddSingleton<IEleccionLiderService>(new CerrojoSiempreConcedidoFalso());
    }

    [Fact]
    public async Task Con_la_cuenta_bloqueada_dice_que_esta_bloqueada_y_no_ofrece_reintentar_el_codigo()
    {
        _signIn.Resultado = SignInResult.LockedOut;
        var cut = Render<LoginCon2fa>();

        await EnviarCodigoAsync(cut);

        var alerta = cut.Find("[role=alert]");
        alerta.TextContent.Should().Contain(TextoBloqueo);
        alerta.TextContent.Should().NotContain(TextoCodigoNoValido,
            "el mensaje de código incorrecto manda a reintentar, y bloqueado no sirve de nada");
        cut.FindAll("#codigo").Should().BeEmpty("con la cuenta bloqueada no hay código que reintentar");
        cut.FindAll("form").Should().BeEmpty("no puede quedar ningún formulario de envío, tenga el campo que tenga");
        cut.Find("a.acceso-entrar").GetAttribute("href").Should().Be("/cuenta/iniciar-sesion");
        _registro.Mensajes.Should().Contain(m => m.Contains("cuenta bloqueada"),
            "el log distingue el bloqueo del código incorrecto");
        _signIn.SesionCerrada.Should().BeTrue(
            "el paso de 2FA pendiente se cierra: desbloqueada la cuenta, volver aquí no debe ahorrar la contraseña");
    }

    /// <summary>Control: sin bloqueo, el código incorrecto sigue diciéndose igual.</summary>
    [Fact]
    public async Task Con_un_codigo_incorrecto_sin_bloqueo_sigue_diciendo_codigo_no_valido()
    {
        _signIn.Resultado = SignInResult.Failed;
        var cut = Render<LoginCon2fa>();

        await EnviarCodigoAsync(cut);

        var alerta = cut.Find("[role=alert]");
        alerta.TextContent.Should().Contain(TextoCodigoNoValido);
        alerta.TextContent.Should().NotContain(TextoBloqueo);
        cut.FindAll("#codigo").Should().ContainSingle("sin bloqueo se puede volver a intentar");
        _registro.Mensajes.Should().NotContain(m => m.Contains("cuenta bloqueada"),
            "un código incorrecto no se registra como bloqueo");
        _signIn.SesionCerrada.Should().BeFalse("sin bloqueo el paso de 2FA sigue abierto para reintentar");
    }

    /// <summary>
    /// La propiedad que demuestra que el cerrojo protege de verdad el
    /// contador de bloqueo: cuando ya hay una verificación en curso para el
    /// mismo usuario, <c>SignInManager</c> no debe tocarse en absoluto — ni
    /// una sola llamada — porque cada llamada a
    /// <c>TwoFactorAuthenticatorSignInAsync</c> con un código, válido o no,
    /// cuenta como un intento real para Identity.
    ///
    /// Que el cerrojo mismo excluya a un competidor concurrente ya está
    /// probado contra PostgreSQL real en
    /// <c>EleccionLiderPostgresServiceTests.Solo_una_replica_gana_el_liderazgo_para_la_misma_clave</c>
    /// (con <c>TaskCompletionSource</c> forzando el solape de verdad — un E2E
    /// con dos POST HTTP "concurrentes" no lo logra de forma fiable: medido,
    /// con el cerrojo retirado del todo, <c>AccessFailedCount</c> seguía
    /// subiendo solo 1 en las tres repeticiones que se probaron, no 2 —
    /// probablemente por el token de concurrencia de Identity absorbiendo la
    /// segunda escritura antes de que el segundo POST llegue a solaparse de
    /// verdad con el primero. Ese test se descartó por no ser sensible: daba
    /// verde con o sin cerrojo). Lo que falta demostrar, y es lo único que
    /// puede fallar por un error de cableado en <c>LoginCon2fa</c>, es que la
    /// página respeta el "false" del cerrojo y no llama a Identity de todos
    /// modos — eso es exactamente lo que prueba este test, sin depender de
    /// ganar ninguna carrera.
    /// </summary>
    [Fact]
    public async Task Con_el_cerrojo_ocupado_no_se_llama_a_SignInManager_y_se_avisa_al_usuario()
    {
        var cerrojo = new CerrojoQueRechazaFalso();
        Services.AddSingleton<IEleccionLiderService>(cerrojo);
        var cut = Render<LoginCon2fa>();

        await EnviarCodigoAsync(cut);

        _signIn.LlamadasVerificacion.Should().Be(0,
            "el segundo envío se rechaza ANTES de tocar SignInManager — no cuenta como intento fallido");
        var alerta = cut.Find("[role=alert]");
        alerta.TextContent.Should().Contain("Ya se está verificando");
        alerta.TextContent.Should().NotContain(TextoCodigoNoValido,
            "no hubo ningún código evaluado: el rechazo es del cerrojo, no de Identity");
        cut.FindAll("#codigo").Should().ContainSingle("el rechazo del cerrojo no cierra el paso de 2FA");
        _registro.Mensajes.Should().Contain(m => m.Contains("ya había una en curso"),
            "el rechazo se audita distinto de un código incorrecto");
    }

    private static async Task EnviarCodigoAsync(IRenderedComponent<LoginCon2fa> cut)
    {
        await cut.Find("#codigo").ChangeAsync(new ChangeEventArgs { Value = "123456" });
        await cut.Find("form").SubmitAsync();
    }

    /// <summary>
    /// <see cref="SignInManager{TUser}"/> es una clase concreta; sus métodos son
    /// virtuales, así que se sustituyen solo los dos que usa la página.
    /// </summary>
    private sealed class SignInManagerFalso : SignInManager<ApplicationUser>
    {
        private static readonly UserManager<ApplicationUser> Usuarios = new(
            new AlmacenSinUso(), Opciones.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(),
            [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);

        private readonly ApplicationUser _pendiente = new() { Id = Guid.NewGuid() };

        public SignInManagerFalso()
            : base(Usuarios, new HttpContextAccessor(),
                new UserClaimsPrincipalFactory<ApplicationUser>(Usuarios, Opciones.Create(new IdentityOptions())),
                Opciones.Create(new IdentityOptions()), NullLogger<SignInManager<ApplicationUser>>.Instance,
                new AuthenticationSchemeProvider(Opciones.Create(new AuthenticationOptions())),
                new DefaultUserConfirmation<ApplicationUser>())
        {
        }

        public SignInResult Resultado { get; set; } = SignInResult.Failed;

        public bool SesionCerrada { get; private set; }

        public int LlamadasVerificacion { get; private set; }

        public override Task SignOutAsync()
        {
            SesionCerrada = true;
            return Task.CompletedTask;
        }

        public override Task<ApplicationUser?> GetTwoFactorAuthenticationUserAsync() =>
            Task.FromResult<ApplicationUser?>(_pendiente);

        public override Task<SignInResult> TwoFactorAuthenticatorSignInAsync(string code, bool isPersistent, bool rememberClient)
        {
            LlamadasVerificacion++;
            return Task.FromResult(Resultado);
        }
    }

    /// <summary>
    /// Cerrojo de prueba que siempre rechaza, como si otra petición ya
    /// estuviera dentro: nunca ejecuta <c>trabajo</c>, así que si
    /// <c>LoginCon2fa</c> llamara a <c>SignInManager</c> de todos modos sería
    /// un error de cableado en la página, no del cerrojo.
    /// </summary>
    private sealed class CerrojoQueRechazaFalso : IEleccionLiderService
    {
        public Task<bool> IntentarEjecutarComoLiderAsync(
            string clave, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    /// <summary>
    /// Cerrojo de prueba que siempre concede: una sola instancia de bUnit
    /// entre los dos envíos no compite consigo misma, así que el cerrojo real
    /// (Postgres) no aporta nada aquí — ejecuta el trabajo directamente, para
    /// no depender de una base de datos en un test de renderizado.
    /// </summary>
    private sealed class CerrojoSiempreConcedidoFalso : IEleccionLiderService
    {
        public async Task<bool> IntentarEjecutarComoLiderAsync(
            string clave, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken)
        {
            await trabajo(cancellationToken);
            return true;
        }
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
}
