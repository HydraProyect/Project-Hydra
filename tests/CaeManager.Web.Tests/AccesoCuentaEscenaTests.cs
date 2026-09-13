using System.Security.Claims;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Account;
using CaeManager.Web.Components.Account.Pages;
using CaeManager.Web.Components.Layout;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
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
        Services.AddSingleton<IEleccionLiderService>(new CerrojoSiempreConcedidoFalso());
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

    /// <summary>
    /// Comprueba el marcado: que la lista de requisitos y el aviso de
    /// coincidencia existen con los atributos que <c>acceso-contrasena.js</c>
    /// necesita para engancharse. bUnit no ejecuta ese guion (ver el
    /// comentario de clase), así que esto NO demuestra que la marca en vivo
    /// funcione — quitar el listener 'input' del guion no lo haría fallar.
    /// Eso es responsabilidad de un E2E.
    /// </summary>
    [Fact]
    public void Restablecer_pinta_la_escena_y_el_marcado_de_requisitos_y_coincidencia()
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

    /// <summary>
    /// Cuenta real, pero <see cref="IEmailService.EnviarAsync"/> falla (Graph
    /// caído, sin configurar, sin red...). Antes esto se registraba en el log y
    /// la pantalla mentía «revisa tu correo» de todos modos; ahora tiene que
    /// avisar del fallo sin decir «no encontramos esa cuenta» — el mensaje no
    /// puede depender de si la cuenta existe.
    /// </summary>
    [Fact]
    public async Task Recuperar_contrasena_cuenta_existente_pero_envio_fallido_avisa_sin_decir_enviado()
    {
        _almacen.Usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "marta.ruiz@consultora.es" };
        Services.AddSingleton<IEmailService>(new EmailServiceConfigurable(
            Result.Fallo(Error.Crear("Email.ErrorRed", "No pudimos enviar el correo."))));

        var cut = Render<OlvideContrasena>();
        await cut.Find("#email").ChangeAsync(new ChangeEventArgs { Value = "marta.ruiz@consultora.es" });
        await cut.Find("form").SubmitAsync();

        cut.Find("h2.acceso-titulo").TextContent.Should().Be("¿Has olvidado la contraseña?",
            "el envío falló de verdad: no puede decir 'Revisa tu correo' como si hubiera salido");
        cut.Find(".acceso-alerta").TextContent.Should().Be("No pudimos procesar tu solicitud. Inténtalo de nuevo en unos minutos.");
    }

    /// <summary>
    /// Fija la no-enumeración para el camino de éxito: cuenta real con envío
    /// correcto acaba en la MISMA pantalla que
    /// <see cref="Recuperar_contrasena_reenvia_con_un_formulario_y_no_con_un_clic_sin_circuito"/>
    /// usa para una cuenta inexistente («Revisa tu correo» en ambas). El fallo
    /// de envío (test de arriba) es la única desviación permitida, y su mensaje
    /// no menciona la existencia de la cuenta.
    /// </summary>
    [Fact]
    public async Task Recuperar_contrasena_cuenta_existente_y_envio_correcto_dice_revisa_tu_correo_igual_que_sin_cuenta()
    {
        _almacen.Usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "marta.ruiz@consultora.es" };
        Services.AddSingleton<IEmailService>(new EmailServiceConfigurable(Result.Exito()));

        var cut = Render<OlvideContrasena>();
        await cut.Find("#email").ChangeAsync(new ChangeEventArgs { Value = "marta.ruiz@consultora.es" });
        await cut.Find("form").SubmitAsync();

        cut.Find("h2.acceso-titulo").TextContent.Should().Be("Revisa tu correo");
    }

    /// <summary>
    /// <see cref="UserManager{TUser}.ResetPasswordAsync"/> tuvo éxito (la
    /// contraseña ya cambió), pero el <c>UpdateAsync</c> que persiste el fin del
    /// cambio obligatorio falla. Antes el resultado se descartaba y la pantalla
    /// anunciaba «Restablecimiento de contraseña correcto» igual; ahora tiene que
    /// enseñar el motivo de Identity y no anunciar el proceso como terminado
    /// (ni cerrar sesión).
    /// </summary>
    [Fact]
    public async Task Restablecer_contrasena_si_falla_persistir_el_usuario_no_dice_correcto()
    {
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "marta.ruiz@consultora.es", DebeCambiarContrasena = true };
        _almacen.Usuario = usuario;
        var token = await _usuarios.GeneratePasswordResetTokenAsync(usuario);
        var codigo = WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(token));
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"/cuenta/restablecer-contrasena?userId={usuario.Id}&code={codigo}");

        _almacen.ResultadoUpdate = IdentityResult.Failed(new IdentityError { Code = "FalloDePrueba", Description = "No se pudo guardar el usuario." });

        var cut = Render<RestablecerContrasena>();
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghi1" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghi1" });
        await cut.Find("form").SubmitAsync();

        cut.FindAll("h2.acceso-titulo").Should().NotContain(h => h.TextContent == "Contraseña actualizada");
        cut.Find(".acceso-alerta").TextContent.Should().Be("No se pudo guardar el usuario.");
    }

    /// <summary>
    /// Mismo defecto que arriba, en la pantalla de cambio obligatorio ya
    /// autenticado: si <c>UpdateAsync</c> falla tras un
    /// <c>ChangePasswordAsync</c> correcto, no se refresca la sesión ni se
    /// navega a "/" como si el cambio hubiera terminado del todo.
    /// </summary>
    [Fact]
    public async Task Cambiar_contrasena_si_falla_persistir_el_usuario_no_navega_ni_refresca_sesion()
    {
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "marta.ruiz@consultora.es", DebeCambiarContrasena = true };
        var contrasenaActual = "Abcdefghi1";
        _almacen.Usuario = usuario;
        _almacen.HashContrasena = new PasswordHasher<ApplicationUser>().HashPassword(usuario, contrasenaActual);
        _almacen.ResultadoUpdate = IdentityResult.Failed(new IdentityError { Code = "FalloDePrueba", Description = "No se pudo guardar el usuario." });

        AddAuthorization().SetAuthorized("marta.ruiz@consultora.es")
            .SetClaims(new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()));

        var navegacion = Services.GetRequiredService<NavigationManager>();
        var uriDePartida = navegacion.Uri;

        var cut = Render<CambiarContrasena>();
        await cut.Find("#password-actual").ChangeAsync(new ChangeEventArgs { Value = contrasenaActual });
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvuts2" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvuts2" });
        await cut.Find("form").SubmitAsync();

        cut.Find(".acceso-alerta").TextContent.Should().Be("No se pudo guardar el usuario.");
        navegacion.Uri.Should().Be(uriDePartida, "un UpdateAsync fallido no puede terminar en NavigateTo(\"/\") como si el cambio hubiera terminado");
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

    /// <summary>
    /// Registra un <see cref="DataProtectorTokenProvider{TUser}"/> real (con un
    /// <see cref="EphemeralDataProtectionProvider"/> en memoria, sin persistencia)
    /// bajo el nombre por defecto — necesario para que Generate/Reset/Verify
    /// PasswordResetToken hagan el viaje de ida y vuelta de verdad en los tests
    /// que ejercitan <c>RestablecerContrasena.GuardarAsync</c>, en vez de lanzar
    /// «no hay proveedor de tokens 'Default' registrado».
    /// </summary>
    private static UserManager<ApplicationUser> CrearUsuarios(IUserStore<ApplicationUser> almacen, IdentityOptions opciones)
    {
        var usuarios = new UserManager<ApplicationUser>(
            almacen, Opciones.Create(opciones), new PasswordHasher<ApplicationUser>(),
            [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);
        usuarios.RegisterTokenProvider(TokenOptions.DefaultProvider,
            new DataProtectorTokenProvider<ApplicationUser>(
                new EphemeralDataProtectionProvider(),
                Opciones.Create(new DataProtectionTokenProviderOptions()),
                NullLogger<DataProtectorTokenProvider<ApplicationUser>>.Instance));
        return usuarios;
    }

    /// <summary>
    /// Un <see cref="HttpContext"/> con <see cref="IAuthenticationService"/> falso
    /// (sin operación) en <c>RequestServices</c> — sin él, <c>SignOutAsync</c> y
    /// <c>RefreshSignInAsync</c> lanzan «HttpContext must not be null» o «Unable to
    /// find the required services» en cuanto una página los llama de verdad, en
    /// vez de en el momento en que el defecto que se prueba debería hacerlos
    /// fallar.
    /// </summary>
    private static IHttpContextAccessor CrearHttpContextAccessor() => new HttpContextAccessor
    {
        HttpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IAuthenticationService>(new AutenticacionSinOperacion())
                .BuildServiceProvider(),
        },
    };

    private sealed class AutenticacionSinOperacion : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private static SignInManager<ApplicationUser> CrearSignIn(UserManager<ApplicationUser> usuarios) => new(
        usuarios, CrearHttpContextAccessor(),
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

    /// <summary>Devuelve siempre el mismo <see cref="Result"/>, para simular un envío que sí se intenta.</summary>
    private sealed class EmailServiceConfigurable(Result resultado) : IEmailService
    {
        public Task<Result> EnviarAsync(string destinatarioEmail, string asunto, string cuerpoHtml, CancellationToken cancellationToken = default) =>
            Task.FromResult(resultado);
    }

    /// <summary>
    /// Lo que las páginas consultan: buscar por id y por correo, contraseña y
    /// security stamp (los necesita Identity para el viaje de ida y vuelta real
    /// de <c>ResetPasswordAsync</c>/<c>ChangePasswordAsync</c>: hashea, rota el
    /// stamp y persiste). También soporta <see cref="UpdateAsync"/> con un
    /// resultado configurable por <see cref="ResultadoUpdate"/>, para probar qué
    /// hace cada página cuando Identity no puede persistir el fin del cambio
    /// obligatorio.
    /// </summary>
    private sealed class AlmacenFalso :
        IUserStore<ApplicationUser>, IUserEmailStore<ApplicationUser>,
        IUserPasswordStore<ApplicationUser>, IUserSecurityStampStore<ApplicationUser>
    {
        private string? _hashContrasena;
        private string _securityStamp = Guid.NewGuid().ToString();

        public ApplicationUser? Usuario { get; set; }
        public IdentityResult ResultadoUpdate { get; set; } = IdentityResult.Success;

        /// <summary>Para sembrar la contraseña inicial directamente en el test, sin pasar por CreateAsync (no soportado).</summary>
        public string? HashContrasena { get => _hashContrasena; set => _hashContrasena = value; }

        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken ct) =>
            Task.FromResult(Usuario is not null && Usuario.Id.ToString() == userId ? Usuario : null);

        public Task<ApplicationUser?> FindByEmailAsync(string normalizedEmail, CancellationToken ct) =>
            Task.FromResult(Usuario is not null && string.Equals(Usuario.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase)
                ? Usuario : null);

        public void Dispose() { }
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.Id.ToString());
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.Email);
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.Email?.ToUpperInvariant());
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken ct) => Task.CompletedTask;
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();

        /// <summary>
        /// Identity ya llama a esto por su cuenta dentro de
        /// ResetPasswordAsync/ChangePasswordAsync, para guardar el hash nuevo y el
        /// security stamp rotado — con <c>DebeCambiarContrasena</c> todavía en su
        /// valor de partida (true en estos tests). Esa llamada siempre tiene
        /// éxito: lo que <see cref="ResultadoUpdate"/> gobierna es solo la
        /// escritura explícita de la página, la que pone el campo a false.
        /// </summary>
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken ct) =>
            Task.FromResult(user.DebeCambiarContrasena ? IdentityResult.Success : ResultadoUpdate);

        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct) => throw new NotSupportedException();
        public Task SetEmailAsync(ApplicationUser user, string? email, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetEmailAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.Email);
        public Task<bool> GetEmailConfirmedAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(true);
        public Task SetEmailConfirmedAsync(ApplicationUser user, bool confirmed, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetNormalizedEmailAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.Email?.ToUpperInvariant());
        public Task SetNormalizedEmailAsync(ApplicationUser user, string? normalizedEmail, CancellationToken ct) => Task.CompletedTask;

        public Task SetPasswordHashAsync(ApplicationUser user, string? passwordHash, CancellationToken ct)
        {
            _hashContrasena = passwordHash;
            return Task.CompletedTask;
        }

        public Task<string?> GetPasswordHashAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(_hashContrasena);
        public Task<bool> HasPasswordAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(_hashContrasena is not null);

        public Task SetSecurityStampAsync(ApplicationUser user, string stamp, CancellationToken ct)
        {
            _securityStamp = stamp;
            return Task.CompletedTask;
        }

        public Task<string?> GetSecurityStampAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult<string?>(_securityStamp);
    }
}
