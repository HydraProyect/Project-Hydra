using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Seguridad.Incidentes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Notificaciones;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Seguridad;
using CaeManager.Web.Components.Account;
using CaeManager.Web.Components.Account.Pages;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    private readonly SesionesCapturadas _sesiones = new();

    public AccesoCuentaEscenaTests()
    {
        Services.AddSeguridadCuentaDePrueba();
        _usuarios = CrearUsuarios(_almacen, PoliticaDeLaApp());

        Services.AddSingleton(_usuarios);
        Services.AddSingleton(CrearSignIn(_usuarios));
        Services.AddSingleton<IOptions<IdentityOptions>>(Opciones.Create(PoliticaDeLaApp()));
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddSingleton<IEmailService>(new EmailQueNoDebeUsarse());
        Services.AddSingleton<IEleccionLiderService>(new CerrojoSiempreConcedidoFalso());
        Services.AddSingleton<IRegistroSesionesUsuario>(_sesiones);
        Services.AddLocalization();
        AddAuthorization();
    }

    [Fact]
    public async Task Sesion_privilegiada_no_cambia_contrasena()
    {
        var seleccion = new SeleccionPrivilegiadaDePrueba();
        Services.AddSingleton<IClienteActivoSeleccionado>(seleccion);
        var avisos = new AvisosCapturados();
        Services.AddSingleton<ILoggerFactory>(new LoggerFactory([avisos]));
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "marta@example.es", DebeCambiarContrasena = true };
        _almacen.Usuario = usuario;
        var hash = new PasswordHasher<ApplicationUser>().HashPassword(usuario, "Abcdefghijk1");
        _almacen.HashContrasena = hash;
        AddAuthorization().SetAuthorized(usuario.Email).SetClaims(new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()));
        var cut = Render<CambiarContrasena>();
        await cut.Find("#password-actual").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghijk1" });
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvutsrqp2" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvutsrqp2" });
        await cut.Find("form").SubmitAsync();
        cut.Find("[role=alert]").TextContent.Should().Contain("Sesión Privilegiada");
        _almacen.HashContrasena.Should().Be(hash);
        usuario.DebeCambiarContrasena.Should().BeTrue();
        avisos.Mensajes.Should().ContainSingle().Which.Should().Contain(seleccion.SesionPrivilegiadaIdSeleccionada!.Value.ToString());
    }

    [Fact]
    public async Task Sesion_privilegiada_no_restablece_contrasena_con_enlace_valido()
    {
        Services.AddSingleton<IClienteActivoSeleccionado>(new SeleccionPrivilegiadaDePrueba());
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "marta@example.es", DebeCambiarContrasena = true };
        _almacen.Usuario = usuario;
        var hash = new PasswordHasher<ApplicationUser>().HashPassword(usuario, "Abcdefghijk1");
        _almacen.HashContrasena = hash;
        var token = await _usuarios.GeneratePasswordResetTokenAsync(usuario);
        var codigo = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/cuenta/restablecer-contrasena?userId={usuario.Id}&code={codigo}");
        var cut = Render<RestablecerContrasena>();
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvutsrqp2" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvutsrqp2" });
        await cut.Find("form").SubmitAsync();
        cut.Find("[role=alert]").TextContent.Should().Contain("Sesión Privilegiada");
        _almacen.HashContrasena.Should().Be(hash);
        usuario.DebeCambiarContrasena.Should().BeTrue();
    }

    public enum SeleccionEnCookie { Viva, SinSesion, DeOtroUsuario, Ilegible, Retirada }

    /// <summary>
    /// Con la clase real, no con un doble: una cookie que nombra una Sesión Privilegiada solo cuenta con la sesión
    /// iniciada, el token legible y de ese usuario, y sin que la revalidación la haya retirado. «Ilegible» es la rama
    /// del token caducado (<c>CryptographicException</c> en <c>LeerCargaUtil</c>).
    /// </summary>
    private static ClienteActivoSeleccionado SeleccionDesdeCookie(SeleccionEnCookie caso, Guid usuarioId)
    {
        var proteccion = new EphemeralDataProtectionProvider();
        var emisor = caso == SeleccionEnCookie.Ilegible ? new EphemeralDataProtectionProvider() : proteccion;
        var usuarioDelToken = caso == SeleccionEnCookie.DeOtroUsuario ? Guid.NewGuid() : usuarioId;
        var contexto = new DefaultHttpContext
        {
            User = caso == SeleccionEnCookie.SinSesion
                ? new ClaimsPrincipal(new ClaimsIdentity())
                : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, usuarioId.ToString())], "test")),
        };
        contexto.Request.Headers.Cookie =
            $"{ClienteActivoSeleccionado.NombreCookie}={ClienteActivoSeleccionado.Proteger(emisor, usuarioDelToken, Guid.NewGuid(), null, Guid.NewGuid())}";

        var seleccion = new ClienteActivoSeleccionado(new HttpContextAccessor { HttpContext = contexto }, proteccion);
        if (caso == SeleccionEnCookie.Retirada)
        {
            seleccion.SesionPrivilegiadaIdSeleccionada.Should().NotBeNull("la selección estaba viva antes de retirarla");
            seleccion.Invalidar();
        }
        return seleccion;
    }

    private async Task<(IRenderedComponent<RestablecerContrasena> Pagina, string HashAnterior)> RestablecerConSeleccionAsync(SeleccionEnCookie caso)
    {
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "marta@example.es" };
        Services.AddSingleton<IClienteActivoSeleccionado>(SeleccionDesdeCookie(caso, usuario.Id));
        _almacen.Usuario = usuario;
        var hash = new PasswordHasher<ApplicationUser>().HashPassword(usuario, "Abcdefghijk1");
        _almacen.HashContrasena = hash;
        Services.GetRequiredService<SignInManager<ApplicationUser>>().Context.RequestServices =
            new ServiceCollection().AddSingleton<IAuthenticationService>(new AutenticacionCapturada(new ClaimsPrincipal(new ClaimsIdentity()))).BuildServiceProvider();
        var token = await _usuarios.GeneratePasswordResetTokenAsync(usuario);
        var codigo = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/cuenta/restablecer-contrasena?userId={usuario.Id}&code={codigo}");

        var cut = Render<RestablecerContrasena>();
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvutsrqp2" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvutsrqp2" });
        await cut.Find("form").SubmitAsync();
        return (cut, hash);
    }

    [Theory]
    [InlineData(SeleccionEnCookie.SinSesion)]
    [InlineData(SeleccionEnCookie.DeOtroUsuario)]
    [InlineData(SeleccionEnCookie.Ilegible)]
    [InlineData(SeleccionEnCookie.Retirada)]
    public async Task Una_cookie_residual_de_Sesion_Privilegiada_no_deja_a_nadie_sin_restablecer_su_contrasena(SeleccionEnCookie caso)
    {
        var (cut, hash) = await RestablecerConSeleccionAsync(caso);

        cut.Find("h2.acceso-titulo").TextContent.Should().Be("Contrase\u00f1a actualizada");
        _almacen.HashContrasena.Should().NotBe(hash);
    }

    [Fact]
    public async Task Una_seleccion_viva_leida_de_la_cookie_si_impide_restablecer_la_contrasena()
    {
        var avisos = new AvisosCapturados();
        Services.AddSingleton<ILoggerFactory>(new LoggerFactory([avisos]));

        var (cut, hash) = await RestablecerConSeleccionAsync(SeleccionEnCookie.Viva);

        cut.Find("[role=alert]").TextContent.Should().Contain("Sesión Privilegiada");
        _almacen.HashContrasena.Should().Be(hash);
        avisos.Mensajes.Should().Contain(m => m.Contains("Sesión Privilegiada"));
    }

    private sealed class AvisosCapturados : ILoggerProvider, ILogger
    {
        public List<string> Mensajes { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Mensajes.Add(formatter(state, exception));
        }

        public void Dispose() { }
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
    [InlineData("Abcdefghijk1")]
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
        items[0].GetAttribute("data-minimo").Should().Be("12");
        items[1].HasAttribute("data-minimo").Should().BeFalse();
        items[0].TextContent.Should().Contain("Al menos 12 caracteres").And.Contain("pendiente");
        items.Should().OnlyContain(li => !li.ClassList.Contains("acceso-requisito-cumplido"));
        vacia.Find("[data-requisitos-resumen=password-nueva]").GetAttribute("aria-live").Should().Be("polite");

        var cumplida = Render<RequisitosContrasena>(p => p
            .Add(r => r.IdCampo, "password-nueva")
            .Add(r => r.Politica, PoliticaDeLaApp().Password)
            .Add(r => r.Valor, "Abcdefghijk1"));

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

    /// <summary>
    /// FS-16: forzado por <c>DebeCambiarContrasena</c>, «Volver» rebotaría en el guard de
    /// MainLayout, así que la única salida es cerrar sesión. Quien llega desde «Mi cuenta»
    /// (<see cref="RutasMiCuenta.CambiarContrasena"/>) no está obligado a nada: se le dice
    /// otra cosa y puede volver sin cambiarla.
    /// </summary>
    [Fact]
    public void Cambiar_contrasena_forzado_no_ofrece_volver()
    {
        var cut = Render<CambiarContrasena>();

        cut.Markup.Should().Contain("Tienes que cambiar tu contraseña antes de continuar.");
        cut.FindAll(".acceso-alterno a").Should().BeEmpty();
        cut.FindAll("form").Should().ContainSingle(f => f.GetAttribute("action") == "/cuenta/cerrar-sesion");
    }

    [Fact]
    public void Cambiar_contrasena_desde_Mi_cuenta_no_dice_que_sea_obligatorio_y_deja_volver()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(RutasMiCuenta.CambiarContrasena);

        var cut = Render<CambiarContrasena>();

        cut.Markup.Should().NotContain("Tienes que cambiar tu contraseña antes de continuar.");
        cut.Markup.Should().Contain("Escribe tu contraseña actual y la que quieres usar a partir de ahora.");
        var volver = cut.Find(".acceso-alterno a");
        volver.GetAttribute("href").Should().Be("/");
        volver.GetAttribute("data-enhance-nav").Should().Be("false", "sale de AccesoLayout a MainLayout");
    }

    [Fact]
    public void La_verificacion_en_dos_pasos_tiene_titulo_y_salida_sin_estar_bloqueada()
    {
        Services.AddSingleton<SignInManager<ApplicationUser>>(new SignInManagerCon2faPendiente(new UsuariosContador(_almacen, PoliticaDeLaApp())));

        var cut = Render<LoginCon2fa>();

        cut.Find("h2.acceso-titulo").TextContent.Should().Be("Verificación en dos pasos");
        cut.Find(".acceso-codigo #codigo").GetAttribute("autocomplete").Should().Be("one-time-code");
        cut.FindAll("button[type=submit]").Should().ContainSingle("Ayudas.IniciarSesionAsync pulsa el único envío tras rellenar #codigo");
        var salidas = cut.FindAll(".acceso-olvido a").Select(a => a.GetAttribute("href")).ToList();
        salidas.Should().Contain("/cuenta/iniciar-sesion");
        // P0-8 (FS-01): sin el móvil, la salida es un código de recuperación.
        salidas.Should().Contain(h => h!.Contains("/cuenta/verificar-2fa?") && h.Contains("modo=recuperacion"));
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
        cut.Find("h2.acceso-titulo").TextContent.Should().Be("¿Olvidaste tu contraseña?");

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

    [Fact]
    public async Task Recuperar_contrasena_envio_fallido_muestra_la_misma_respuesta_generica()
    {
        _almacen.Usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "marta.ruiz@consultora.es" };
        var correo = new EmailServiceConfigurable(Result.Fallo(Error.Crear("Email.ErrorRed", "No pudimos enviar el correo.")));
        Services.AddSingleton<IEmailService>(correo);

        var cut = Render<OlvideContrasena>();
        await cut.Find("#email").ChangeAsync(new ChangeEventArgs { Value = "marta.ruiz@consultora.es" });
        await cut.Find("form").SubmitAsync();

        cut.Find("h2.acceso-titulo").TextContent.Should().Be("Revisa tu correo");
        var recuperacion = Services.GetRequiredService<CaeManager.Web.Services.RecuperacionContrasena>();
        await recuperacion.StartAsync(CancellationToken.None);
        try
        {
            await correo.Intento.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cut.Find("h2.acceso-titulo").TextContent.Should().Be("Revisa tu correo");
            cut.FindAll(".acceso-alerta").Should().BeEmpty();
        }
        finally
        {
            await recuperacion.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Fija la no-enumeración para el camino de éxito: cuenta real con envío
    /// correcto acaba en la MISMA pantalla que
    /// <see cref="Recuperar_contrasena_reenvia_con_un_formulario_y_no_con_un_clic_sin_circuito"/>
    /// usa para una cuenta inexistente («Revisa tu correo» en ambas). El fallo
    /// de envío conserva también esa misma respuesta genérica.
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
    /// DECISIÓN D: <c>DebeCambiarContrasena=false</c> se fija ANTES de
    /// <c>ResetPasswordAsync</c>, sobre la misma instancia — así Identity la
    /// persiste en la MISMA actualización que la contraseña, sin un
    /// <c>UpdateAsync</c> posterior separado. Si esa única actualización
    /// falla (aquí, vía <see cref="AlmacenFalso.ResultadoUpdate"/>), la
    /// operación entera falla: no hay ya un escenario donde "la contraseña
    /// cambió pero la bandera no se persistió" — ese estado parcial es
    /// justo lo que la consolidación elimina. La pantalla tiene que enseñar
    /// el motivo de Identity y no anunciar el proceso como terminado (ni
    /// cerrar sesión).
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
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghijk1" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghijk1" });
        await cut.Find("form").SubmitAsync();

        cut.FindAll("h2.acceso-titulo").Should().NotContain(h => h.TextContent == "Contraseña actualizada");
        cut.Find(".acceso-alerta").TextContent.Should().Be("No se pudo guardar el usuario.");
    }

    /// <summary>
    /// Mismo razonamiento que arriba, en la pantalla de cambio obligatorio ya
    /// autenticado: la bandera se fija antes de <c>ChangePasswordAsync</c>, así
    /// que si la única actualización falla, <c>ChangePasswordAsync</c> falla
    /// entero y no se refresca la sesión ni se navega a "/" como si el cambio
    /// hubiera terminado.
    /// </summary>
    [Fact]
    public async Task Cambiar_contrasena_si_falla_persistir_el_usuario_no_navega_ni_refresca_sesion()
    {
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "marta.ruiz@consultora.es", DebeCambiarContrasena = true };
        var contrasenaActual = "Abcdefghijk1";
        _almacen.Usuario = usuario;
        _almacen.HashContrasena = new PasswordHasher<ApplicationUser>().HashPassword(usuario, contrasenaActual);
        _almacen.ResultadoUpdate = IdentityResult.Failed(new IdentityError { Code = "FalloDePrueba", Description = "No se pudo guardar el usuario." });

        AddAuthorization().SetAuthorized("marta.ruiz@consultora.es")
            .SetClaims(new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()));

        var navegacion = Services.GetRequiredService<NavigationManager>();
        var uriDePartida = navegacion.Uri;

        var cut = Render<CambiarContrasena>();
        await cut.Find("#password-actual").ChangeAsync(new ChangeEventArgs { Value = contrasenaActual });
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvutsrqp2" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvutsrqp2" });
        await cut.Find("form").SubmitAsync();

        cut.Find(".acceso-alerta").TextContent.Should().Be("No se pudo guardar el usuario.");
        navegacion.Uri.Should().Be(uriDePartida, "un UpdateAsync fallido no puede terminar en NavigateTo(\"/\") como si el cambio hubiera terminado");
    }

    [Fact]
    public async Task Cambiar_contrasena_cuenta_fallos_y_respeta_el_bloqueo_temporal()
    {
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "marta@example.es", DebeCambiarContrasena = true, LockoutEnabled = true };
        _almacen.Usuario = usuario;
        _almacen.HashContrasena = new PasswordHasher<ApplicationUser>().HashPassword(usuario, "Abcdefghi1");
        AddAuthorization().SetAuthorized(usuario.Email).SetClaims(new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()));
        var cut = Render<CambiarContrasena>();
        await cut.Find("#password-actual").ChangeAsync(new ChangeEventArgs { Value = "incorrecta" });
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvutsrqp2" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvutsrqp2" });
        await cut.Find("form").SubmitAsync();
        usuario.AccessFailedCount.Should().Be(1);
        usuario.DebeCambiarContrasena.Should().BeTrue();
        var mensaje = cut.Find(".acceso-alerta").TextContent;
        usuario.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(5);
        await cut.Find("form").SubmitAsync();
        usuario.AccessFailedCount.Should().Be(1);
        cut.Find(".acceso-alerta").TextContent.Should().Be(mensaje);
        usuario.LockoutEnd = DateTimeOffset.UtcNow.AddSeconds(-1);
        await cut.Find("form").SubmitAsync();
        usuario.AccessFailedCount.Should().Be(2);
    }

    [Fact]
    public async Task Cambiar_contrasena_con_la_cuenta_bloqueada_no_cambia_aunque_la_actual_sea_correcta_y_al_acertar_pone_el_contador_a_cero()
    {
        var usuario = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = "marta@example.es",
            LockoutEnabled = true,
            AccessFailedCount = 2,
            LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(5),
        };
        _almacen.Usuario = usuario;
        var hashInicial = new PasswordHasher<ApplicationUser>().HashPassword(usuario, "Abcdefghi1");
        _almacen.HashContrasena = hashInicial;
        AddAuthorization().SetAuthorized(usuario.Email).SetClaims(new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()));
        var cut = Render<CambiarContrasena>();
        await cut.Find("#password-actual").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghi1" });
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvutsrqp2" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvutsrqp2" });

        await cut.Find("form").SubmitAsync();

        _almacen.HashContrasena.Should().Be(hashInicial, "con la cuenta bloqueada no se llega a ChangePasswordAsync");
        cut.Find(".acceso-alerta").TextContent.Should().Contain("No se pudo verificar la contraseña actual o la cuenta está bloqueada.");

        usuario.LockoutEnd = DateTimeOffset.UtcNow.AddSeconds(-1);
        await cut.Find("form").SubmitAsync();

        _almacen.HashContrasena.Should().NotBe(hashInicial);
        usuario.AccessFailedCount.Should().Be(0, "acertar la contraseña actual pone el contador a cero, como un login correcto");
    }

    /// <summary>
    /// DECISIÓN D: cerrojo no bloqueante compartido <c>credencial:{userId}</c>,
    /// adquirido ANTES de <c>ResetPasswordAsync</c>. Con el cerrojo ocupado, el
    /// rechazo tiene que pasar ANTES de tocar Identity — cero llamadas, ni un
    /// intento fallido ni uno con éxito. Mismo patrón que
    /// <c>VerificacionDosPasosBloqueoTests.Con_el_cerrojo_ocupado_no_se_llama_a_SignInManager_y_se_avisa_al_usuario</c>.
    /// </summary>
    [Fact]
    public async Task Restablecer_contrasena_con_el_cerrojo_ocupado_no_llama_a_ResetPasswordAsync()
    {
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "marta.ruiz@consultora.es", DebeCambiarContrasena = true };
        _almacen.Usuario = usuario;
        var usuariosContador = new UsuariosContador(_almacen, PoliticaDeLaApp());
        var token = await usuariosContador.GeneratePasswordResetTokenAsync(usuario);
        var codigo = WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(token));

        var cerrojo = new CerrojoQueRechazaFalso();
        Services.AddSingleton<UserManager<ApplicationUser>>(usuariosContador);
        Services.AddSingleton<IEleccionLiderService>(cerrojo);
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"/cuenta/restablecer-contrasena?userId={usuario.Id}&code={codigo}");

        var cut = Render<RestablecerContrasena>();
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghijk1" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghijk1" });
        await cut.Find("form").SubmitAsync();

        usuariosContador.LlamadasReset.Should().Be(0, "el rechazo del cerrojo pasa ANTES de tocar Identity");
        cut.Find(".acceso-alerta").TextContent.Should().Be(
            "Ya hay una operación sobre esta cuenta en curso. Espera un momento e inténtalo de nuevo.");
        usuario.DebeCambiarContrasena.Should().BeTrue(
            "con el cerrojo ocupado no se ejecuta el delegado: ni siquiera se toca la bandera en memoria");
        cerrojo.ClaveRecibida.Should().Be($"credencial:{usuario.Id}",
            "la clave compartida con CambiarContrasena — y explícitamente NO con verificar-2fa:{userId}");
    }

    /// <summary>Mismo cerrojo compartido <c>credencial:{userId}</c>, en la pantalla de cambio ya autenticado.</summary>
    [Fact]
    public async Task Cambiar_contrasena_con_el_cerrojo_ocupado_no_llama_a_ChangePasswordAsync()
    {
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "marta.ruiz@consultora.es", DebeCambiarContrasena = true };
        var contrasenaActual = "Abcdefghijk1";
        _almacen.Usuario = usuario;
        _almacen.HashContrasena = new PasswordHasher<ApplicationUser>().HashPassword(usuario, contrasenaActual);
        var usuariosContador = new UsuariosContador(_almacen, PoliticaDeLaApp());
        var cerrojo = new CerrojoQueRechazaFalso();
        Services.AddSingleton<UserManager<ApplicationUser>>(usuariosContador);
        Services.AddSingleton<IEleccionLiderService>(cerrojo);

        AddAuthorization().SetAuthorized("marta.ruiz@consultora.es")
            .SetClaims(new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()));

        var navegacion = Services.GetRequiredService<NavigationManager>();
        var uriDePartida = navegacion.Uri;

        var cut = Render<CambiarContrasena>();
        await cut.Find("#password-actual").ChangeAsync(new ChangeEventArgs { Value = contrasenaActual });
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvuts2Zzz" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvuts2Zzz" });
        await cut.Find("form").SubmitAsync();

        usuariosContador.LlamadasCambio.Should().Be(0, "el rechazo del cerrojo pasa ANTES de tocar Identity");
        cut.Find(".acceso-alerta").TextContent.Should().Be(
            "Ya hay una operación sobre esta cuenta en curso. Espera un momento e inténtalo de nuevo.");
        navegacion.Uri.Should().Be(uriDePartida, "sin ejecutar el delegado no hay ni refresco de sesión ni navegación");
        cerrojo.ClaveRecibida.Should().Be($"credencial:{usuario.Id}",
            "la clave compartida con RestablecerContrasena — y explícitamente NO con verificar-2fa:{userId}");
        usuario.DebeCambiarContrasena.Should().BeTrue(
            "con el cerrojo ocupado no se ejecuta el delegado: ni siquiera se toca la bandera en memoria");
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

    /// <summary>
    /// El formulario pedía MinLength(8) y la política de Identity exige 12: una
    /// contraseña de 9 pasaba la validación del formulario y llegaba a Identity. Ahora
    /// se comprueba con la misma política (IdentityOptions), antes de tocar Identity.
    /// </summary>
    [Fact]
    public async Task Cambiar_contrasena_rechaza_antes_de_Identity_lo_que_la_politica_no_admite()
    {
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "marta.ruiz@consultora.es", DebeCambiarContrasena = true };
        var contrasenaActual = "Abcdefghijk1";
        _almacen.Usuario = usuario;
        _almacen.HashContrasena = new PasswordHasher<ApplicationUser>().HashPassword(usuario, contrasenaActual);

        AddAuthorization().SetAuthorized("marta.ruiz@consultora.es")
            .SetClaims(new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()));

        var cut = Render<CambiarContrasena>();
        await cut.Find("#password-actual").ChangeAsync(new ChangeEventArgs { Value = contrasenaActual });
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvuts2" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvuts2" });
        await cut.Find("form").SubmitAsync();

        cut.Find(".acceso-alerta").TextContent.Should().Be("La contraseña nueva no cumple los requisitos: al menos 12 caracteres.");
        usuario.DebeCambiarContrasena.Should().BeTrue("no se ha tocado la cuenta");
    }

    [Fact]
    public void Cambiar_contrasena_ofrece_cerrar_sesion_como_salida()
    {
        Services.AddScoped<Microsoft.AspNetCore.Components.Forms.AntiforgeryStateProvider, AntiforgeryDePrueba>();
        AddAuthorization().SetAuthorized("marta.ruiz@consultora.es");

        var cut = Render<CambiarContrasena>();

        var salida = cut.FindAll("form").Single(f => f.GetAttribute("action") == "/cuenta/cerrar-sesion");
        salida.GetAttribute("method").Should().Be("post");
        salida.QuerySelector("button")!.TextContent.Trim().Should().Be("Cerrar sesión");
    }

    private sealed class AntiforgeryDePrueba : Microsoft.AspNetCore.Components.Forms.AntiforgeryStateProvider
    {
        public override Microsoft.AspNetCore.Components.Forms.AntiforgeryRequestToken? GetAntiforgeryToken() =>
            new("token-de-prueba", "__RequestVerificationToken");
    }

    /// <summary>La de InfrastructureServiceCollectionExtensions: 12 caracteres, sin símbolo.</summary>
    private static IdentityOptions PoliticaDeLaApp()
    {
        var opciones = new IdentityOptions();
        opciones.Password.RequiredLength = 12;
        opciones.Password.RequireNonAlphanumeric = false;
        return opciones;
    }

    [Fact]
    public async Task Recuperacion_responde_sin_buscar_la_cuenta_ni_esperar_el_correo()
    {
        var correo = new CorreoRegistrado();
        Services.AddSingleton<IEmailService>(correo);
        var conCuenta = Render<OlvideContrasena>();
        _almacen.Usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "ana@example.test" };
        await conCuenta.Find("#email").ChangeAsync(new ChangeEventArgs { Value = "ana@example.test" });
        await conCuenta.Find("form").SubmitAsync();

        var sinCuenta = Render<OlvideContrasena>();
        await sinCuenta.Find("#email").ChangeAsync(new ChangeEventArgs { Value = "nadie@example.test" });
        await sinCuenta.Find("form").SubmitAsync();

        sinCuenta.Find("h2.acceso-titulo").TextContent.Should().Be(conCuenta.Find("h2.acceso-titulo").TextContent);
        sinCuenta.Find(".acceso-explica").TextContent.Should().Be(conCuenta.Find(".acceso-explica").TextContent);
        _almacen.BusquedasCorreo.Should().Be(0);
        correo.Cuerpos.Should().BeEmpty();
    }

    [Fact]
    public async Task Recuperacion_rechaza_un_correo_mas_largo_que_una_direccion_sin_normalizarlo_ni_encolarlo()
    {
        var correo = new CorreoRegistrado();
        Services.AddSingleton<IEmailService>(correo);
        var cut = Render<OlvideContrasena>();
        var largo = new string('a', 300) + "@example.test";
        await cut.Find("#email").ChangeAsync(new ChangeEventArgs { Value = largo });
        await cut.Find("form").SubmitAsync();

        cut.Markup.Should().Contain("Introduce un correo válido.");
        cut.FindAll("h2.acceso-titulo").Should().NotContain(h => h.TextContent == "Revisa tu correo");

        var recuperacion = Services.GetRequiredService<CaeManager.Web.Services.RecuperacionContrasena>();
        recuperacion.Solicitar("BARRERA@EXAMPLE.TEST", new Uri("https://cuentas.example/"));
        await recuperacion.StartAsync(CancellationToken.None);
        try
        {
            await _almacen.Barrera.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _almacen.BusquedasCorreo.Should().Be(1, "solo la solicitud de control llegó a la cola");
            correo.Cuerpos.Should().BeEmpty();
        }
        finally
        {
            await recuperacion.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Recuperacion_limita_tres_por_hora_y_correo_normalizado_con_respuesta_identica()
    {
        Services.AddSingleton<NavigationManager>(new NavegacionRecuperacionManipulada());
        var reloj = new RelojRecuperacion();
        var correo = new CorreoRegistrado();
        Services.AddSingleton<TimeProvider>(reloj);
        Services.AddSingleton<IEmailService>(correo);
        _almacen.Usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "ana@example.test" };
        string? respuesta = null;
        foreach (var email in new[] { "ana@example.test", "ANA@example.test", "ana@EXAMPLE.test", "ana@example.test" })
        {
            var ultimo = Render<OlvideContrasena>();
            await ultimo.Find("#email").ChangeAsync(new ChangeEventArgs { Value = email });
            await ultimo.Find("form").SubmitAsync();
            var visible = ultimo.Find("h2.acceso-titulo").TextContent + ultimo.Find(".acceso-explica").TextContent;
            respuesta ??= visible;
            visible.Should().Be(respuesta);
        }

        var recuperacion = Services.GetRequiredService<CaeManager.Web.Services.RecuperacionContrasena>();
        recuperacion.Solicitar("BARRERA@EXAMPLE.TEST", new Uri("https://cuentas.example/"));
        await recuperacion.StartAsync(CancellationToken.None);
        try
        {
            await _almacen.Barrera.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _almacen.BusquedasCorreo.Should().Be(4);
            await correo.EsperarAsync(3);
            correo.Cuerpos.Should().HaveCount(3).And.OnlyContain(c => c.Contains("https://cuentas.example/cuenta/restablecer-contrasena?"));
            reloj.Ahora += TimeSpan.FromHours(1);
            recuperacion.Solicitar("ANA@EXAMPLE.TEST", new Uri("https://cuentas.example/"));
            await correo.EsperarAsync(1);
            correo.Cuerpos.Should().HaveCount(4);
        }
        finally
        {
            await recuperacion.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Recuperacion_sin_url_publica_fuera_de_development_no_busca_ni_envia()
    {
        Services.AddSingleton(SeguridadCuentaPruebas.Enlaces(null));
        var correo = new CorreoRegistrado();
        Services.AddSingleton<IEmailService>(correo);
        _almacen.Usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "ana@example.test" };
        var cut = Render<OlvideContrasena>();
        await cut.Find("#email").ChangeAsync(new ChangeEventArgs { Value = "ana@example.test" });
        await cut.Find("form").SubmitAsync();
        cut.Find("h2.acceso-titulo").TextContent.Should().Be("Revisa tu correo");
        var recuperacion = Services.GetRequiredService<CaeManager.Web.Services.RecuperacionContrasena>();
        recuperacion.Solicitar("BARRERA@EXAMPLE.TEST", new Uri("https://cuentas.example/"));
        await recuperacion.StartAsync(CancellationToken.None);
        try
        {
            await _almacen.Barrera.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _almacen.BusquedasCorreo.Should().Be(1);
            correo.Cuerpos.Should().BeEmpty();
        }
        finally
        {
            await recuperacion.StopAsync(CancellationToken.None);
        }
    }

    private sealed class NavegacionRecuperacionManipulada : NavigationManager
    {
        public NavegacionRecuperacionManipulada() => Initialize("https://host-manipulado.example/", "https://host-manipulado.example/");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }
    private sealed class RelojRecuperacion : TimeProvider
    {
        public DateTimeOffset Ahora { get; set; } = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Ahora;
    }

    private sealed class CorreoRegistrado : IEmailService
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Cuerpos { get; } = new();
        private readonly System.Threading.Channels.Channel<bool> _envios = System.Threading.Channels.Channel.CreateUnbounded<bool>();
        public async Task EsperarAsync(int cantidad)
        {
            for (var i = 0; i < cantidad; i++)
                await _envios.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        public Task<Result> EnviarAsync(string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo,
            string? responderA = null, CancellationToken cancellationToken = default)
        {
            Cuerpos.Enqueue(cuerpoHtml);
            _envios.Writer.TryWrite(true);
            return Task.FromResult(Result.Exito());
        }
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

    /// <summary>
    /// V3.3.3 en el cambio voluntario: ChangePasswordAsync rota el sello (las demás sesiones
    /// dejan de validar), las demás sesiones se revocan, esta se conserva, y RefreshSignInAsync
    /// reemite su cookie con el sello nuevo.
    /// La contraseña actual tiene 10 caracteres: las anteriores a la política de 12 siguen valiendo.
    /// </summary>
    [Fact]
    public async Task Cambiar_contrasena_rota_el_sello_revoca_las_demas_y_reemite_la_sesion_actual()
    {
        var sid = Guid.NewGuid();
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "marta@example.test" };
        _almacen.Usuario = usuario;
        _almacen.HashContrasena = new PasswordHasher<ApplicationUser>().HashPassword(usuario, "Abcdefghi1");
        var selloAnterior = await _usuarios.GetSecurityStampAsync(usuario);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()),
             new Claim("AspNet.Identity.SecurityStamp", selloAnterior!)], "test"));
        var autenticacion = new AutenticacionCapturada(principal);
        AddAuthorization().SetAuthorized(usuario.Email!).SetClaims(
            new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()),
            new Claim(SignInManagerCuentaDesactivada.ClaimSid, sid.ToString("N")));
        var signIn = Services.GetRequiredService<SignInManager<ApplicationUser>>();
        signIn.Context.RequestServices = new ServiceCollection().AddSingleton<IAuthenticationService>(autenticacion).BuildServiceProvider();

        var cut = Render<CambiarContrasena>();
        await cut.Find("#password-actual").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghi1" });
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghijk1" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghijk1" });
        await cut.Find("form").SubmitAsync();

        var selloNuevo = await _usuarios.GetSecurityStampAsync(usuario);
        selloNuevo.Should().NotBe(selloAnterior);
        (await signIn.ValidateSecurityStampAsync(principal)).Should().BeNull();
        autenticacion.Emitido.Should().NotBeNull();
        autenticacion.Emitido!.FindFirstValue("AspNet.Identity.SecurityStamp").Should().Be(selloNuevo);
        _sesiones.DemasCerradas.Should().Equal([(usuario.Id, (Guid?)sid)], "se conserva solo la sesi\u00f3n de quien cambia su contrase\u00f1a");
        _sesiones.CerradasTodas.Should().BeEmpty();
    }

    /// <summary>
    /// Fallo en cerrado, como <see cref="CierreSesionEnServidor"/>: sin revocar las demás sesiones
    /// no se reemite la cookie ni se sigue como si hubiera ido bien.
    /// </summary>
    [Fact]
    public async Task Cambiar_contrasena_si_no_se_revocan_las_demas_falla_y_no_reemite_la_sesion()
    {
        _sesiones.Lanza = true;
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "marta@example.test" };
        _almacen.Usuario = usuario;
        _almacen.HashContrasena = new PasswordHasher<ApplicationUser>().HashPassword(usuario, "Abcdefghijk1");
        var autenticacion = new AutenticacionCapturada(null);
        AddAuthorization().SetAuthorized(usuario.Email!).SetClaims(new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()));
        Services.GetRequiredService<SignInManager<ApplicationUser>>().Context.RequestServices =
            new ServiceCollection().AddSingleton<IAuthenticationService>(autenticacion).BuildServiceProvider();

        var cut = Render<CambiarContrasena>();
        await cut.Find("#password-actual").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghijk1" });
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvutsrqp2" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvutsrqp2" });
        var enviar = () => cut.Find("form").SubmitAsync();

        await enviar.Should().ThrowAsync<InvalidOperationException>().WithMessage("fallo simulado");
        autenticacion.Emitido.Should().BeNull();
    }

    /// <summary>Con la contraseña actual incorrecta no cambia nada: ninguna sesión se revoca.</summary>
    [Fact]
    public async Task Cambiar_contrasena_rechazado_no_revoca_ninguna_sesion()
    {
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "marta@example.test" };
        _almacen.Usuario = usuario;
        _almacen.HashContrasena = new PasswordHasher<ApplicationUser>().HashPassword(usuario, "Abcdefghijk1");
        AddAuthorization().SetAuthorized(usuario.Email!).SetClaims(new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()));

        var cut = Render<CambiarContrasena>();
        await cut.Find("#password-actual").ChangeAsync(new ChangeEventArgs { Value = "Incorrecta123" });
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvutsrqp2" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvutsrqp2" });
        await cut.Find("form").SubmitAsync();

        _sesiones.DemasCerradas.Should().BeEmpty();
    }

    /// <summary>
    /// V3.3.3 en el restablecimiento por enlace: ResetPasswordAsync rota el sello (las
    /// sesiones abiertas en otros navegadores dejan de validar), todas las sesiones de la
    /// cuenta se revocan y la página cierra además la del navegador donde se usó el enlace,
    /// sin reemitir ninguna cookie.
    /// </summary>
    [Fact]
    public async Task Restablecer_rota_el_sello_y_cierra_la_sesion_del_navegador_sin_reemitirla()
    {
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "marta@example.test" };
        _almacen.Usuario = usuario;
        var selloAnterior = await _usuarios.GetSecurityStampAsync(usuario);
        var token = await _usuarios.GeneratePasswordResetTokenAsync(usuario);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()),
             new Claim("AspNet.Identity.SecurityStamp", selloAnterior!)], "test"));
        var autenticacion = new AutenticacionCapturada(principal);
        Services.GetRequiredService<SignInManager<ApplicationUser>>().Context.RequestServices =
            new ServiceCollection().AddSingleton<IAuthenticationService>(autenticacion).BuildServiceProvider();
        var codigo = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/cuenta/restablecer-contrasena?userId={usuario.Id}&code={codigo}");

        var cut = Render<RestablecerContrasena>();
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghijk1" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghijk1" });
        await cut.Find("form").SubmitAsync();

        cut.Find("h2.acceso-titulo").TextContent.Should().Be("Contrase\u00f1a actualizada");
        (await _usuarios.GetSecurityStampAsync(usuario)).Should().NotBe(selloAnterior);
        (await Services.GetRequiredService<SignInManager<ApplicationUser>>().ValidateSecurityStampAsync(principal))
            .Should().BeNull("la sesi\u00f3n con el sello anterior cae en la siguiente revalidaci\u00f3n");
        autenticacion.Emitido.Should().BeNull("el enlace no abre ni conserva sesi\u00f3n");
        autenticacion.Cierres.Should().BeGreaterThan(0, "la p\u00e1gina cierra la sesi\u00f3n del navegador");
        _sesiones.CerradasTodas.Should().Equal([usuario.Id], "sin revocar, una cookie robada se refresca dentro del intervalo de revalidaci\u00f3n");
    }

    /// <summary>
    /// Fallo en cerrado, como <see cref="CierreSesionEnServidor"/>: la página no dice «actualizada»
    /// con las sesiones sin revocar. La contraseña ya cambió y este enlace no vuelve a servir: las
    /// sesiones se revocan con otro restablecimiento, desde un enlace nuevo.
    /// </summary>
    [Fact]
    public async Task Restablecer_si_no_se_revocan_las_sesiones_falla_y_no_lo_da_por_hecho()
    {
        _sesiones.Lanza = true;
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "marta@example.test" };
        _almacen.Usuario = usuario;
        var token = await _usuarios.GeneratePasswordResetTokenAsync(usuario);
        var codigo = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/cuenta/restablecer-contrasena?userId={usuario.Id}&code={codigo}");
        var autenticacion = new AutenticacionCapturada(null);
        Services.GetRequiredService<SignInManager<ApplicationUser>>().Context.RequestServices =
            new ServiceCollection().AddSingleton<IAuthenticationService>(autenticacion).BuildServiceProvider();

        var cut = Render<RestablecerContrasena>();
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghijk1" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghijk1" });
        var enviar = () => cut.Find("form").SubmitAsync();

        await enviar.Should().ThrowAsync<InvalidOperationException>().WithMessage("fallo simulado");
        cut.Markup.Should().NotContain("Contrase\u00f1a actualizada");
        autenticacion.Cierres.Should().Be(0);
    }

    /// <summary>Un enlace rechazado no cambia la credencial: no revoca las sesiones de nadie.</summary>
    [Fact]
    public async Task Restablecer_con_token_invalido_no_revoca_ninguna_sesion()
    {
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "marta@example.test" };
        _almacen.Usuario = usuario;
        var codigo = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("token-inventado"));
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/cuenta/restablecer-contrasena?userId={usuario.Id}&code={codigo}");

        var cut = Render<RestablecerContrasena>();
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghijk1" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghijk1" });
        await cut.Find("form").SubmitAsync();

        _sesiones.CerradasTodas.Should().BeEmpty();
    }

    /// <summary>
    /// «No he sido yo» quita la contraseña y revoca las sesiones, no solo rota el sello; después salen
    /// la señal de incidente, el enlace de contraseña nueva y el aviso al Administrador.
    /// </summary>
    [Fact]
    public async Task No_he_sido_yo_revoca_las_sesiones_y_da_senal_correo_y_aviso()
    {
        var escena = new EscenaNoHeSidoYo(this);

        var resultado = await escena.ConfirmarAsync();

        resultado.Should().Be(ResultadoNoHeSidoYo.Protegida);
        _sesiones.CerradasTodas.Should().Equal([escena.Usuario.Id]);
        escena.Senales.NoReconocidos.Should().Equal([(escena.Usuario.Id, escena.Usuario.TenantId, true)]);
        var correoPersona = escena.Correos.Enviados.Should().ContainSingle(c => c.Destinatario == escena.Usuario.Email).Subject;
        correoPersona.Asunto.Should().StartWith("Tu cuenta está protegida");
        correoPersona.Cuerpo.Should().Contain("/cuenta/restablecer-contrasena").And.Contain("Hemos cerrado todas tus sesiones");
        escena.Correos.Enviados.Should().ContainSingle(c => c.Destinatario == EscenaNoHeSidoYo.CorreoAdministrador)
            .Which.Cuerpo.Should().Contain("Se han cerrado sus sesiones");
        var aviso = escena.Contexto.Guardadas.Should().ContainSingle().Subject;
        aviso.UsuarioDestinatarioId.Should().Be(EscenaNoHeSidoYo.Administrador);
        aviso.Titulo.Should().Be("Cuenta protegida por «no he sido yo»");
        escena.Registro.Errores.Should().BeEmpty();
    }

    /// <summary>
    /// La contraseña ya no está y el enlace ya no sirve cuando falla la revocación: la señal de seguridad,
    /// el enlace y el aviso salen igualmente, el fallo queda como error y nadie lee que las sesiones
    /// estén cerradas.
    /// </summary>
    [Fact]
    public async Task No_he_sido_yo_si_no_se_revocan_las_sesiones_da_senal_correo_y_aviso_sin_decir_que_estan_cerradas()
    {
        _sesiones.Lanza = true;
        var escena = new EscenaNoHeSidoYo(this);

        var resultado = await escena.ConfirmarAsync();

        resultado.Should().Be(ResultadoNoHeSidoYo.SesionesSinCerrar);
        _sesiones.CerradasTodas.Should().Equal([escena.Usuario.Id]);
        escena.Registro.Errores.Should().ContainSingle().Which.Should().Contain("sin poder cerrar las sesiones");
        escena.Senales.NoReconocidos.Should().Equal([(escena.Usuario.Id, escena.Usuario.TenantId, true)]);
        var correoPersona = escena.Correos.Enviados.Should().ContainSingle(c => c.Destinatario == escena.Usuario.Email).Subject;
        correoPersona.Asunto.Should().NotContain("protegida");
        correoPersona.Cuerpo.Should().Contain("/cuenta/restablecer-contrasena")
            .And.Contain("no hemos podido cerrar").And.NotContain("Hemos cerrado");
        escena.Correos.Enviados.Should().ContainSingle(c => c.Destinatario == EscenaNoHeSidoYo.CorreoAdministrador)
            .Which.Cuerpo.Should().Contain("No se han podido cerrar sus sesiones");
        var aviso = escena.Contexto.Guardadas.Should().ContainSingle().Subject;
        aviso.UsuarioDestinatarioId.Should().Be(EscenaNoHeSidoYo.Administrador);
        aviso.Titulo.Should().NotContain("protegida");
    }

    /// <summary>
    /// Devolver 0 se leería como «no tenía sesiones» y quien llama diría que están cerradas. Revocar una
    /// sola no cambia: dice que no había una viva.
    /// </summary>
    [Fact]
    public async Task Cerrar_las_sesiones_de_una_cuenta_que_no_se_ve_lanza_en_vez_de_devolver_cero()
    {
        var registro = new RegistroSesionesUsuario(null!, _usuarios, null!, NullLogger<RegistroSesionesUsuario>.Instance);
        var cuenta = Guid.NewGuid();

        await FluentActions.Awaiting(() => registro.CerrarTodasAsync(cuenta)).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => registro.CerrarLasDemasAsync(cuenta, null)).Should().ThrowAsync<InvalidOperationException>();
        (await registro.RevocarAsync(cuenta, Guid.NewGuid())).Should().BeFalse();
    }

    /// <summary>
    /// El servicio real de «No he sido yo», sin servidor de base de datos: la lectura de Administradores
    /// la responde un interceptor con uno solo, y lo que se iba a guardar se queda en
    /// <see cref="ContextoSinServidor.Guardadas"/>. Quién es Administrador lo mide la integración
    /// (<c>FronteraCuentaFirmadaTests</c>).
    /// </summary>
    private sealed class EscenaNoHeSidoYo
    {
        public static readonly Guid Administrador = Guid.NewGuid();
        public const string CorreoAdministrador = "administrador@example.test";

        private readonly VigilanciaInicioSesionService _vigilancia;

        public EscenaNoHeSidoYo(AccesoCuentaEscenaTests pruebas)
        {
            Usuario = new ApplicationUser { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), Email = "marta@example.test" };
            pruebas._almacen.Usuario = Usuario;
            pruebas._almacen.HashContrasena = new PasswordHasher<ApplicationUser>().HashPassword(Usuario, "Abcdefghijk1");
            pruebas._usuarios.RegisterTokenProvider(ProveedorTokenNoHeSidoYo.Nombre, new TokenSiempreValido());

            var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
                .UseNpgsql("Host=localhost;Database=sin-servidor;Username=x;Password=x")
                .AddInterceptors(new ConexionSinServidor(), new LecturaDeAdministrador())
                .Options;
            Contexto = new ContextoSinServidor(opciones);

            var ambitos = new ServiceCollection()
                .AddSingleton(pruebas._usuarios)
                .AddSingleton<IRegistroSesionesUsuario>(pruebas._sesiones)
                .AddSingleton<IEmailService>(Correos)
                .AddSingleton<CaeManagerDbContext>(Contexto)
                .BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>();

            var peticion = new DefaultHttpContext();
            peticion.Request.Scheme = "https";
            peticion.Request.Host = new HostString("app.example.test");
            _vigilancia = new VigilanciaInicioSesionService(
                new HttpContextAccessor { HttpContext = peticion }, ambitos, null!, null!, null!, null!, Registro, Senales);
        }

        public ApplicationUser Usuario { get; }
        public ContextoSinServidor Contexto { get; }
        public SenalesCapturadas Senales { get; } = new();
        public CorreosCapturados Correos { get; } = new();
        public RegistroCapturado Registro { get; } = new();

        public Task<ResultadoNoHeSidoYo> ConfirmarAsync() =>
            _vigilancia.ConfirmarNoHeSidoYoAsync(Usuario.Id.ToString(), WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("token")));
    }

    private sealed class ConexionSinServidor : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed class LecturaDeAdministrador : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var filas = new DataTable();
            filas.Columns.Add("Id", typeof(Guid));
            filas.Columns.Add("Email", typeof(string));
            filas.Columns.Add("LockoutEnd", typeof(DateTimeOffset));
            filas.Rows.Add(EscenaNoHeSidoYo.Administrador, EscenaNoHeSidoYo.CorreoAdministrador, DBNull.Value);
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(filas.CreateDataReader()));
        }
    }

    private sealed class ContextoSinServidor(DbContextOptions<CaeManagerDbContext> opciones)
        : CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), new TenantEfectivoDePrueba(null))
    {
        public List<NotificacionUsuario> Guardadas { get; } = [];

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            Guardadas.AddRange(ChangeTracker.Entries<NotificacionUsuario>().Select(e => e.Entity));
            return Task.FromResult(Guardadas.Count);
        }
    }

    private sealed class SenalesCapturadas : ISenalesIncidenteSeguridad
    {
        public List<(Guid UsuarioId, Guid TenantId, bool TeniaContrasena)> NoReconocidos { get; } = [];

        public Task InicioSesionNoReconocidoAsync(Guid usuarioId, Guid tenantPropietarioId, bool teniaContrasena, CancellationToken ct = default)
        {
            NoReconocidos.Add((usuarioId, tenantPropietarioId, teniaContrasena));
            return Task.CompletedTask;
        }

        public Task ExportacionAsync(Guid actorRealUsuarioId, Guid? tenantId, int filas, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DocumentoSensibleAbiertoAsync(Guid actorRealUsuarioId, Guid? tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AccesoAOtroTenantRechazadoAsync(Guid usuarioId, Guid tenantOrigenId, Guid tenantObjetivoId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class CorreosCapturados : IEmailService
    {
        public List<(string Destinatario, string Asunto, string Cuerpo)> Enviados { get; } = [];

        public Task<Result> EnviarAsync(string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo, string? responderA = null, CancellationToken cancellationToken = default)
        {
            Enviados.Add((destinatarioEmail, asunto, cuerpoHtml));
            return Task.FromResult(Result.Exito());
        }
    }

    private sealed class RegistroCapturado : ILogger<VigilanciaInicioSesionService>
    {
        public List<string> Errores { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                Errores.Add(formatter(state, exception));
        }
    }

    private sealed class TokenSiempreValido : IUserTwoFactorTokenProvider<ApplicationUser>
    {
        public Task<string> GenerateAsync(string purpose, UserManager<ApplicationUser> manager, ApplicationUser user) => Task.FromResult("token");
        public Task<bool> ValidateAsync(string purpose, string token, UserManager<ApplicationUser> manager, ApplicationUser user) => Task.FromResult(true);
        public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<ApplicationUser> manager, ApplicationUser user) => Task.FromResult(false);
    }

    /// <summary>Solo lo que hacen las páginas de contraseña. Cada llamada queda anotada aunque luego lance.</summary>
    private sealed class SesionesCapturadas : IRegistroSesionesUsuario
    {
        public bool Lanza { get; set; }
        public List<Guid> CerradasTodas { get; } = [];
        public List<(Guid UsuarioId, Guid? SidConservada)> DemasCerradas { get; } = [];

        public Task<int> CerrarTodasAsync(Guid usuarioId, CancellationToken ct = default)
        {
            CerradasTodas.Add(usuarioId);
            return Lanza ? throw new InvalidOperationException("fallo simulado") : Task.FromResult(1);
        }

        public Task<int> CerrarLasDemasAsync(Guid usuarioId, Guid? sidConservada, CancellationToken ct = default)
        {
            DemasCerradas.Add((usuarioId, sidConservada));
            return Lanza ? throw new InvalidOperationException("fallo simulado") : Task.FromResult(1);
        }

        public Task<bool> EstaVigenteAsync(Guid usuarioId, Guid sid, CancellationToken ct = default) => Task.FromResult(true);
        public Task RegistrarAsync(Guid usuarioId, Guid tenantPropietarioId, Guid sid, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RevocarAsync(Guid usuarioId, Guid sid, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class AutenticacionCapturada(ClaimsPrincipal? principal) : IAuthenticationService
    {
        public ClaimsPrincipal? Emitido { get; private set; }
        public int Cierres { get; private set; }
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(principal is null ? AuthenticateResult.NoResult() : AuthenticateResult.Success(new AuthenticationTicket(principal, new AuthenticationProperties(), IdentityConstants.ApplicationScheme)));
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) { Emitido = principal; return Task.CompletedTask; }
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) { Cierres++; return Task.CompletedTask; }
    }

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
        public Task<Result> EnviarAsync(string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo, string? responderA = null, CancellationToken cancellationToken = default) =>
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

    /// <summary>
    /// Cerrojo de prueba que siempre rechaza, como si otra petición ya
    /// estuviera dentro: nunca ejecuta <c>trabajo</c>, así que si la página
    /// llamara a Identity de todos modos sería un error de cableado en la
    /// página, no del cerrojo. Mismo patrón que
    /// <c>VerificacionDosPasosBloqueoTests.CerrojoQueRechazaFalso</c>. Guarda
    /// la <see cref="ClaveRecibida"/> para comprobar que las dos pantallas
    /// usan la MISMA clave compartida <c>credencial:{userId}</c> — hallazgo de
    /// revisión (Codex): sin esto, el test pasa igual aunque cada pantalla
    /// usara un prefijo distinto.
    /// </summary>
    private sealed class CerrojoQueRechazaFalso : IEleccionLiderService
    {
        public string? ClaveRecibida { get; private set; }

        public Task<bool> IntentarEjecutarComoLiderAsync(
            string clave, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken)
        {
            ClaveRecibida = clave;
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// <see cref="UserManager{TUser}.ResetPasswordAsync"/> y
    /// <see cref="UserManager{TUser}.ChangePasswordAsync"/> son virtuales;
    /// contar las llamadas reales es la única forma de demostrar que, con el
    /// cerrojo ocupado, la página rechaza ANTES de tocar Identity — no basta
    /// con observar el mensaje de error, que también podría salir de un
    /// camino que sí llamó y descartó el resultado.
    /// </summary>
    private sealed class UsuariosContador : UserManager<ApplicationUser>
    {
        public UsuariosContador(IUserStore<ApplicationUser> almacen, IdentityOptions opciones) : base(
            almacen, Opciones.Create(opciones), new PasswordHasher<ApplicationUser>(),
            [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
            NullLogger<UserManager<ApplicationUser>>.Instance)
        {
            RegisterTokenProvider(TokenOptions.DefaultProvider,
                new DataProtectorTokenProvider<ApplicationUser>(
                    new EphemeralDataProtectionProvider(),
                    Opciones.Create(new DataProtectionTokenProviderOptions()),
                    NullLogger<DataProtectorTokenProvider<ApplicationUser>>.Instance));
        }

        public int LlamadasReset { get; private set; }
        public int LlamadasCambio { get; private set; }

        public override Task<bool> GetTwoFactorEnabledAsync(ApplicationUser user) => Task.FromResult(true);

        public override Task<IdentityResult> ResetPasswordAsync(ApplicationUser user, string token, string newPassword)
        {
            LlamadasReset++;
            return base.ResetPasswordAsync(user, token, newPassword);
        }

        public override Task<IdentityResult> ChangePasswordAsync(ApplicationUser user, string currentPassword, string newPassword)
        {
            LlamadasCambio++;
            return base.ChangePasswordAsync(user, currentPassword, newPassword);
        }
    }

    /// <summary>Devuelve siempre el mismo <see cref="Result"/>, para simular un envío que sí se intenta.</summary>
    private sealed class EmailServiceConfigurable(Result resultado) : IEmailService
    {
        public TaskCompletionSource Intento { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Result> EnviarAsync(string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo, string? responderA = null, CancellationToken cancellationToken = default) =>
            RegistrarIntento();
        private Task<Result> RegistrarIntento()
        {
            Intento.TrySetResult();
            return Task.FromResult(resultado);
        }
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
        IUserPasswordStore<ApplicationUser>, IUserSecurityStampStore<ApplicationUser>, IUserLockoutStore<ApplicationUser>
    {
        private string? _hashContrasena;
        private string _securityStamp = Guid.NewGuid().ToString();

        public ApplicationUser? Usuario { get; set; }
        public IdentityResult ResultadoUpdate { get; set; } = IdentityResult.Success;
        public Task<DateTimeOffset?> GetLockoutEndDateAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.LockoutEnd);
        public Task SetLockoutEndDateAsync(ApplicationUser user, DateTimeOffset? end, CancellationToken ct) { user.LockoutEnd = end; return Task.CompletedTask; }
        public Task<int> IncrementAccessFailedCountAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(++user.AccessFailedCount);
        public Task ResetAccessFailedCountAsync(ApplicationUser user, CancellationToken ct) { user.AccessFailedCount = 0; return Task.CompletedTask; }
        public Task<int> GetAccessFailedCountAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.AccessFailedCount);
        public Task<bool> GetLockoutEnabledAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.LockoutEnabled);
        public Task SetLockoutEnabledAsync(ApplicationUser user, bool enabled, CancellationToken ct) { user.LockoutEnabled = enabled; return Task.CompletedTask; }

        /// <summary>Para sembrar la contraseña inicial directamente en el test, sin pasar por CreateAsync (no soportado).</summary>
        public string? HashContrasena { get => _hashContrasena; set => _hashContrasena = value; }

        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken ct) =>
            Task.FromResult(Usuario is not null && Usuario.Id.ToString() == userId ? Usuario : null);

        private int _busquedasCorreo;
        public int BusquedasCorreo => Volatile.Read(ref _busquedasCorreo);
        public TaskCompletionSource Barrera { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ApplicationUser?> FindByEmailAsync(string normalizedEmail, CancellationToken ct)
        {
            Interlocked.Increment(ref _busquedasCorreo);
            if (normalizedEmail == "BARRERA@EXAMPLE.TEST") Barrera.TrySetResult();
            return Task.FromResult(Usuario is not null && string.Equals(Usuario.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase)
                ? Usuario : null);
        }

        public void Dispose() { }
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.Id.ToString());
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.Email);
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.Email?.ToUpperInvariant());
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken ct) => Task.CompletedTask;
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();

        /// <summary>
        /// Identity ya llama a esto por su cuenta, UNA sola vez, dentro de
        /// ResetPasswordAsync/ChangePasswordAsync — DECISIÓN D consolidó ahí
        /// la persistencia de <c>DebeCambiarContrasena</c> (la página la fija
        /// ANTES de llamar, sobre la misma instancia), así que ya no hay una
        /// segunda escritura explícita separada. <see cref="ResultadoUpdate"/>
        /// gobierna esa única llamada.
        /// </summary>
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken ct) =>
            Task.FromResult(ResultadoUpdate);

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
