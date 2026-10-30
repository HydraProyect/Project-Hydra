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
        Services.AddLocalization();
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

    // ---------- Código de recuperación (P0-8, FS-01) ----------
    //
    // SignInManager.TwoFactorRecoveryCodeSignInAsync ni mira el bloqueo ni cuenta
    // los fallos; la página hace las dos cosas. Sin ellas, un código de
    // recuperación sería la puerta trasera de una cuenta bloqueada por intentos
    // y un canal de prueba sin límite.

    [Fact]
    public async Task Un_codigo_de_recuperacion_valido_entra_sin_contar_un_fallo()
    {
        _signIn.ResultadoRecuperacion = SignInResult.Success;
        var cut = RenderizarModoRecuperacion();

        await EnviarRecuperacionAsync(cut, "abcde-12345");

        _signIn.CodigosRecuperacionProbados.Should().Equal("abcde-12345");
        _signIn.Usuarios.Fallos.Should().Be(0);
        _registro.Mensajes.Should().Contain(m => m.Contains("correcta con código de recuperación"));
    }

    [Fact]
    public async Task Un_codigo_de_recuperacion_incorrecto_cuenta_un_intento_fallido()
    {
        _signIn.ResultadoRecuperacion = SignInResult.Failed;
        var cut = RenderizarModoRecuperacion();

        await EnviarRecuperacionAsync(cut, "zzzzz-zzzzz");

        _signIn.Usuarios.Fallos.Should().Be(1, "cada código probado gasta un intento, como el de la aplicación");
        cut.Find("[role=alert]").TextContent.Should().Contain("Código de recuperación no válido");
        cut.FindAll("#recuperacion").Should().ContainSingle("sin bloqueo se puede volver a intentar");
        _signIn.SesionCerrada.Should().BeFalse();
    }

    [Fact]
    public async Task Con_la_cuenta_ya_bloqueada_el_codigo_de_recuperacion_ni_se_prueba()
    {
        _signIn.Usuarios.Bloqueado = true;
        _signIn.ResultadoRecuperacion = SignInResult.Success;
        var cut = RenderizarModoRecuperacion();

        await EnviarRecuperacionAsync(cut, "abcde-12345");

        _signIn.CodigosRecuperacionProbados.Should().BeEmpty(
            "un código válido no puede abrir una cuenta bloqueada por intentos");
        cut.Find("[role=alert]").TextContent.Should().Contain(TextoBloqueo);
        cut.FindAll("form").Should().BeEmpty();
        _signIn.SesionCerrada.Should().BeTrue();
    }

    [Fact]
    public async Task El_fallo_que_agota_los_intentos_dice_bloqueada_y_cierra_el_paso()
    {
        _signIn.ResultadoRecuperacion = SignInResult.Failed;
        _signIn.Usuarios.BloquearTrasFallos = 1;
        var cut = RenderizarModoRecuperacion();

        await EnviarRecuperacionAsync(cut, "zzzzz-zzzzz");

        cut.Find("[role=alert]").TextContent.Should().Contain(TextoBloqueo);
        _signIn.SesionCerrada.Should().BeTrue();
    }

    /// <summary>
    /// Revisión Codex: si el intento fallido no se pudo registrar (concurrencia
    /// optimista), seguir en el paso permitiría probar códigos sin gastar intentos.
    /// </summary>
    [Fact]
    public async Task Un_fallo_que_no_se_pudo_registrar_cierra_el_paso()
    {
        _signIn.ResultadoRecuperacion = SignInResult.Failed;
        _signIn.Usuarios.RegistroFalla = true;
        var cut = RenderizarModoRecuperacion();

        await EnviarRecuperacionAsync(cut, "zzzzz-zzzzz");

        cut.Find("[role=alert]").TextContent.Should().Contain("No pudimos registrar el intento");
        cut.FindAll("form").Should().BeEmpty();
        _signIn.SesionCerrada.Should().BeTrue();
    }

    [Fact]
    public async Task Un_codigo_de_recuperacion_vacio_no_gasta_un_intento()
    {
        var cut = RenderizarModoRecuperacion();

        await EnviarRecuperacionAsync(cut, "   ");

        _signIn.CodigosRecuperacionProbados.Should().BeEmpty();
        _signIn.Usuarios.Fallos.Should().Be(0);
        cut.Find("[role=alert]").TextContent.Should().Contain("Introduce un código de recuperación.");
    }

    // ── Primera vinculación de Microsoft sin TOTP (decisión de 2026-10-28) ─────

    /// <summary>
    /// TwoFactorAuthenticatorSignInAsync no mira si el TOTP está activado: sin esta guarda,
    /// una clave sin activar serviría para entrar. Sin marca de alta no hay nada que verificar.
    /// </summary>
    [Fact]
    public void Sin_TOTP_activado_y_sin_marca_de_alta_no_se_ofrece_verificar()
    {
        _signIn.Usuarios.TotpActivado = false;

        var cut = Render<LoginCon2fa>();

        cut.FindAll("#codigo").Should().BeEmpty();
        cut.FindAll("#codigo-alta").Should().BeEmpty();
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/cuenta/iniciar-sesion");
    }

    [Fact]
    public async Task Con_marca_de_alta_se_ensena_una_clave_nueva_y_no_la_verificacion()
    {
        _signIn.Usuarios.TotpActivado = false;

        var cut = RenderizarAlta("GET");

        cut.FindAll("#codigo").Should().BeEmpty("durante el alta no se verifica ni se vincula");
        cut.FindAll("#codigo-alta").Should().ContainSingle();
        _signIn.Usuarios.Reinicios.Should().Be(1, "una clave de un alta abandonada no se reutiliza");

        await EnviarCodigoAltaAsync(cut, "000000");

        cut.Find("[role=alert]").Should().NotBeNull();
        _signIn.Usuarios.Activaciones.Should().Be(0, "un código incorrecto no activa el TOTP");
        _signIn.Usuarios.Vinculos.Should().BeEmpty();

        await EnviarCodigoAltaAsync(cut, UsuariosConBloqueoFalso.CodigoValido);

        _signIn.Usuarios.Activaciones.Should().Be(1);
        _signIn.Usuarios.Vinculos.Should().BeEmpty("el vínculo se guarda solo al superar la verificación");
        _signIn.LlamadasVerificacion.Should().Be(0, "activar no emite sesión");
    }

    [Fact]
    public void El_POST_del_alta_no_cambia_la_clave_enseñada()
    {
        _signIn.Usuarios.TotpActivado = false;

        RenderizarAlta("POST");

        _signIn.Usuarios.Reinicios.Should().Be(0, "cambiarla invalidaría el código que se está enviando");
    }

    [Fact]
    public async Task Superado_el_TOTP_del_alta_se_vincula_y_se_termina_en_la_pagina_de_la_2FA()
    {
        _signIn.Resultado = SignInResult.Success;
        Services.GetRequiredService<NavigationManager>().NavigateTo("/cuenta/verificar-2fa?ReturnUrl=%2Fdocumentos");
        var cut = RenderizarAlta("POST");

        await EnviarCodigoAsync(cut);

        _signIn.Usuarios.Vinculos.Should().ContainSingle().Which.ProviderKey.Should().Be("sub-1");
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("/cuenta/configurar-2fa").And.Contain("returnUrl=%2Fdocumentos",
            "los códigos de recuperación nacen con la 2FA y necesitan la sesión recién emitida");
    }

    /// <summary>
    /// La recarga tras activar conserva la marca de alta en la cookie: con el TOTP ya activo
    /// pide el código y no toca la clave, que es la del autenticador recién enlazado.
    /// </summary>
    [Fact]
    public void Con_el_TOTP_ya_activado_la_marca_de_alta_no_cambia_la_clave_y_pide_el_codigo()
    {
        var cut = RenderizarAlta("GET");

        _signIn.Usuarios.Reinicios.Should().Be(0);
        cut.FindAll("#codigo").Should().ContainSingle();
        cut.FindAll("#codigo-alta").Should().BeEmpty();
    }

    [Fact]
    public void Si_no_se_puede_reiniciar_la_clave_no_se_ensena_ninguna()
    {
        _signIn.Usuarios.TotpActivado = false;
        _signIn.Usuarios.ReinicioFalla = true;

        var cut = RenderizarAlta("GET");

        cut.FindAll("#codigo-alta").Should().BeEmpty("una clave anterior pudo verla otra persona");
        cut.Find("[role=alert]").TextContent.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Si_no_se_puede_activar_el_TOTP_no_se_avanza()
    {
        _signIn.Usuarios.TotpActivado = false;
        _signIn.Usuarios.ActivacionFalla = true;
        var cut = RenderizarAlta("GET");

        await EnviarCodigoAltaAsync(cut, UsuariosConBloqueoFalso.CodigoValido);

        cut.Find("[role=alert]").TextContent.Should().Contain("fallo");
        _signIn.Usuarios.TotpActivado.Should().BeFalse();
        Services.GetRequiredService<NavigationManager>().Uri.Should().NotContain("configurar-2fa");
    }

    private IRenderedComponent<LoginCon2fa> RenderizarAlta(string metodo)
    {
        var principal = InicioSesionMicrosoft.PrincipalSegundoFactorPendiente(Guid.NewGuid(),
            new UserLoginInfo(IdentityEndpointsExtensions.EsquemaMicrosoft, "sub-1", "Microsoft"), altaTotp: true);
        var contexto = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IAuthenticationService>(new AutenticacionDosPasosFalsa(principal))
                .BuildServiceProvider(),
        };
        contexto.Request.Method = metodo;
        return Render<LoginCon2fa>(p => p.AddCascadingValue(contexto));
    }

    private static async Task EnviarCodigoAltaAsync(IRenderedComponent<LoginCon2fa> cut, string codigo)
    {
        await cut.Find("#codigo-alta").ChangeAsync(new ChangeEventArgs { Value = codigo });
        await cut.Find("form").SubmitAsync();
    }

    private sealed class AutenticacionDosPasosFalsa(System.Security.Claims.ClaimsPrincipal principal) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, scheme ?? string.Empty)));
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, System.Security.Claims.ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private IRenderedComponent<LoginCon2fa> RenderizarModoRecuperacion()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/cuenta/verificar-2fa?modo=recuperacion");
        var cut = Render<LoginCon2fa>();
        cut.FindAll("#codigo").Should().BeEmpty("control del instrumento: estamos en el modo de recuperación");
        return cut;
    }

    private static async Task EnviarRecuperacionAsync(IRenderedComponent<LoginCon2fa> cut, string codigo)
    {
        await cut.Find("#recuperacion").ChangeAsync(new ChangeEventArgs { Value = codigo });
        await cut.Find("form").SubmitAsync();
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
        private readonly ApplicationUser _pendiente = new() { Id = Guid.NewGuid() };

        public SignInManagerFalso() : this(new UsuariosConBloqueoFalso())
        {
        }

        private SignInManagerFalso(UsuariosConBloqueoFalso usuarios)
            : base(usuarios, new HttpContextAccessor(),
                new UserClaimsPrincipalFactory<ApplicationUser>(usuarios, Opciones.Create(new IdentityOptions())),
                Opciones.Create(new IdentityOptions()), NullLogger<SignInManager<ApplicationUser>>.Instance,
                new AuthenticationSchemeProvider(Opciones.Create(new AuthenticationOptions())),
                new DefaultUserConfirmation<ApplicationUser>())
        {
            Usuarios = usuarios;
        }

        public UsuariosConBloqueoFalso Usuarios { get; }

        public SignInResult Resultado { get; set; } = SignInResult.Failed;

        public SignInResult ResultadoRecuperacion { get; set; } = SignInResult.Failed;

        public List<string> CodigosRecuperacionProbados { get; } = [];

        public override Task<SignInResult> TwoFactorRecoveryCodeSignInAsync(string recoveryCode)
        {
            CodigosRecuperacionProbados.Add(recoveryCode);
            return Task.FromResult(ResultadoRecuperacion);
        }

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

    /// <summary>
    /// Solo el bloqueo y el contador de fallos, que es lo que la página consulta
    /// en el modo de recuperación; el almacén no se usa.
    /// </summary>
    private sealed class UsuariosConBloqueoFalso() : UserManager<ApplicationUser>(
        new AlmacenSinUso(), Opciones.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(),
        [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
        NullLogger<UserManager<ApplicationUser>>.Instance)
    {
        public bool Bloqueado { get; set; }
        public int Fallos { get; private set; }
        public int? BloquearTrasFallos { get; set; }
        public bool RegistroFalla { get; set; }

        // Quien llega a la verificación tiene el TOTP activado, salvo la primera vinculación
        // de Microsoft que aún lo tiene que dar de alta.
        public bool TotpActivado { get; set; } = true;
        public int Reinicios { get; private set; }
        public int Activaciones { get; private set; }
        public bool ReinicioFalla { get; set; }
        public bool ActivacionFalla { get; set; }
        public List<UserLoginInfo> Vinculos { get; } = [];
        public const string ClaveAutenticador = "JBSWY3DPEHPK3PXP";
        public const string CodigoValido = "654321";

        public override Task<bool> IsLockedOutAsync(ApplicationUser user) => Task.FromResult(Bloqueado);

        public override Task<bool> GetTwoFactorEnabledAsync(ApplicationUser user) => Task.FromResult(TotpActivado);

        public override Task<IdentityResult> ResetAuthenticatorKeyAsync(ApplicationUser user)
        {
            Reinicios++;
            return Task.FromResult(ReinicioFalla ? IdentityResult.Failed() : IdentityResult.Success);
        }

        public override Task<string?> GetAuthenticatorKeyAsync(ApplicationUser user) => Task.FromResult<string?>(ClaveAutenticador);

        public override Task<bool> VerifyTwoFactorTokenAsync(ApplicationUser user, string tokenProvider, string token) =>
            Task.FromResult(token == CodigoValido);

        public override Task<IdentityResult> SetTwoFactorEnabledAsync(ApplicationUser user, bool enabled)
        {
            if (ActivacionFalla) return Task.FromResult(IdentityResult.Failed(new IdentityError { Description = "fallo" }));
            Activaciones++;
            TotpActivado = enabled;
            return Task.FromResult(IdentityResult.Success);
        }

        public override Task<IList<UserLoginInfo>> GetLoginsAsync(ApplicationUser user) =>
            Task.FromResult<IList<UserLoginInfo>>([.. Vinculos]);

        public override Task<ApplicationUser?> FindByLoginAsync(string loginProvider, string providerKey) =>
            Task.FromResult<ApplicationUser?>(null);

        public override Task<IdentityResult> AddLoginAsync(ApplicationUser user, UserLoginInfo login)
        {
            Vinculos.Add(login);
            return Task.FromResult(IdentityResult.Success);
        }

        public override Task<IdentityResult> AccessFailedAsync(ApplicationUser user)
        {
            // Como UpdateAsync al perder la concurrencia optimista: no cuenta y lo dice.
            if (RegistroFalla)
                return Task.FromResult(IdentityResult.Failed(new IdentityErrorDescriber().ConcurrencyFailure()));
            Fallos++;
            if (Fallos >= BloquearTrasFallos) Bloqueado = true;
            return Task.FromResult(IdentityResult.Success);
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
