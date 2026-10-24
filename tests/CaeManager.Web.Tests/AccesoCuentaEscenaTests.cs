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
        Services.AddSeguridadCuentaDePrueba();
        _usuarios = CrearUsuarios(_almacen, PoliticaDeLaApp());

        Services.AddSingleton(_usuarios);
        Services.AddSingleton(CrearSignIn(_usuarios));
        Services.AddSingleton<IOptions<IdentityOptions>>(Opciones.Create(PoliticaDeLaApp()));
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddSingleton<IEmailService>(new EmailQueNoDebeUsarse());
        Services.AddSingleton<IEleccionLiderService>(new CerrojoSiempreConcedidoFalso());
        Services.AddLocalization();
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
        Services.AddSingleton<SignInManager<ApplicationUser>>(new SignInManagerCon2faPendiente(_usuarios));

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
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghi1" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghi1" });
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
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvutsr2" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvutsr2" });
        await cut.Find("form").SubmitAsync();

        cut.Find(".acceso-alerta").TextContent.Should().Be("No se pudo guardar el usuario.");
        navegacion.Uri.Should().Be(uriDePartida, "un UpdateAsync fallido no puede terminar en NavigateTo(\"/\") como si el cambio hubiera terminado");
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
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghi1" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Abcdefghi1" });
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
        var contrasenaActual = "Abcdefghi1";
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
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvuts2Z" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvuts2Z" });
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
    /// El formulario pedía MinLength(8) y la política de Identity exige 10: una
    /// contraseña de 9 pasaba la validación del formulario y llegaba a Identity. Ahora
    /// se comprueba con la misma política (IdentityOptions), antes de tocar Identity.
    /// </summary>
    [Fact]
    public async Task Cambiar_contrasena_rechaza_antes_de_Identity_lo_que_la_politica_no_admite()
    {
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), Email = "marta.ruiz@consultora.es", DebeCambiarContrasena = true };
        var contrasenaActual = "Abcdefghi1";
        _almacen.Usuario = usuario;
        _almacen.HashContrasena = new PasswordHasher<ApplicationUser>().HashPassword(usuario, contrasenaActual);

        AddAuthorization().SetAuthorized("marta.ruiz@consultora.es")
            .SetClaims(new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()));

        var cut = Render<CambiarContrasena>();
        await cut.Find("#password-actual").ChangeAsync(new ChangeEventArgs { Value = contrasenaActual });
        await cut.Find("#password-nueva").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvuts2" });
        await cut.Find("#password-confirmar").ChangeAsync(new ChangeEventArgs { Value = "Zyxwvuts2" });
        await cut.Find("form").SubmitAsync();

        cut.Find(".acceso-alerta").TextContent.Should().Be("La contraseña nueva no cumple los requisitos: al menos 10 caracteres.");
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

    /// <summary>La de InfrastructureServiceCollectionExtensions: 10 caracteres, sin símbolo.</summary>
    private static IdentityOptions PoliticaDeLaApp()
    {
        var opciones = new IdentityOptions();
        opciones.Password.RequiredLength = 10;
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
