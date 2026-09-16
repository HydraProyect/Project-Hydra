using System.Reflection;
using System.Security.Claims;
using System.Text;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Web.Components.Account.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Opciones = Microsoft.Extensions.Options.Options;

namespace CaeManager.IntegrationTests.Identity;

/// <summary>
/// Identity/EF reales contra PostgreSQL para el incremento de DECISIÓN D
/// (restablecer/cambiar contraseña, doble envío): consolidar
/// <c>DebeCambiarContrasena</c> en la MISMA actualización que la contraseña
/// (sin un <c>UpdateAsync</c> separado), sobre <c>RestablecerContrasena</c> y
/// <c>CambiarContrasena</c>.
///
/// <para>
/// Se instancian las páginas reales y se invocan por reflexión
/// <c>OnInitialized[Async]</c>/<c>GuardarAsync</c>/<c>CambiarContrasenaAsync</c>
/// — mismo patrón que
/// <c>SelectorTemaGuardadoTrasEscrituraConcurrenteTests</c>: no hace falta
/// renderer (ninguno de los dos métodos llama a <c>StateHasChanged</c>), y así
/// se ejercita el código de producción tal cual, con un <c>UserManager</c>
/// real contra Postgres real (no el <c>AlmacenFalso</c> en memoria de
/// <c>CaeManager.Web.Tests</c>) — necesario porque lo que hay que demostrar es
/// PERSISTENCIA real (una relectura desde un <c>DbContext</c> nuevo, no el
/// mismo objeto en memoria) y el comportamiento real de tokens de un solo uso
/// de Identity 10.0.11, no solo lo que hace el código C# de la página.
/// </para>
///
/// <para>
/// El cerrojo de servidor (<c>credencial:{userId}</c>) NO se ejercita aquí con
/// Postgres real — <c>EleccionLiderPostgresServiceTests</c> ya prueba la
/// exclusión entre réplicas, y no se duplica. Aquí se usa un cerrojo de
/// prueba que siempre concede, para poder atribuir cualquier fallo a Identity
/// o al código de la página, nunca al cerrojo.
/// </para>
/// </summary>
public class RestablecerYCambiarContrasenaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private ServiceProvider _servicios = null!;

    public async Task InitializeAsync()
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());

        // AspNetUsers no lleva filtro global de tenant (ver ApplicationUser.TenantId),
        // pero CaeManagerDbContext exige la dependencia igual para el resto de
        // entidades — ninguno de los caminos bajo prueba la consulta.
        servicios.AddSingleton<ITenantActual>(new SinTenantActual());

        servicios.AddDbContext<CaeManagerDbContext>(opciones => opciones
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL")));

        servicios.AddScoped<PuertaAccesoDatos>();
        servicios.AddScoped<IDesenganchadorDeEntidadesRastreadas>(sp => sp.GetRequiredService<CaeManagerDbContext>());

        servicios.AddIdentityCore<ApplicationUser>()
            .AddEntityFrameworkStores<CaeManagerDbContext>()
            .AddDefaultTokenProviders();

        _servicios = servicios.BuildServiceProvider();

        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    /// <summary>
    /// Reproduce el hueco que la DECISIÓN D cierra: <c>ResetPasswordAsync</c>
    /// tuvo éxito y luego se pierde el enlace por reutilizar el mismo token.
    /// La página real, invocada dos veces con ámbitos (scopes) independientes
    /// — como dos peticiones HTTP reales con el mismo enlace de correo — para
    /// que el segundo intento relea el usuario desde Postgres, no desde el
    /// objeto en memoria del primero.
    ///
    /// <para>
    /// <b>Sensibilidad de la aserción de consolidación</b> (verificada a mano:
    /// verde → mutación → rojo por el motivo esperado → revertir): quitar
    /// «<c>_usuario.DebeCambiarContrasena = false;</c>» de
    /// <c>RestablecerContrasena.razor.cs</c> (manteniendo retirado el segundo
    /// <c>UpdateAsync</c>) hace que la aserción
    /// <c>DebeCambiarContrasena.Should().BeFalse()</c> de abajo falle con
    /// <c>true</c> — exactamente el estado parcial que la consolidación
    /// elimina.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Restablecer_con_el_mismo_token_dos_veces_la_segunda_falla_por_InvalidToken_sin_afectar_el_primer_exito()
    {
        const string ContrasenaNuevaPrimerIntento = "Passw0rd!2024";
        const string ContrasenaNuevaSegundoIntento = "OtraDistinta9!";

        using var ambitoSiembra = _servicios.CreateScope();
        var usuario = await CrearUsuarioAsync(ambitoSiembra.ServiceProvider);
        var token = await ambitoSiembra.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .GeneratePasswordResetTokenAsync(usuario);
        var codigo = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

        // Primer "request": el código de producción tal cual, con el token
        // recién emitido — tiene que tener éxito.
        var cerrojo1 = new CerrojoSiempreConcedidoFalso();
        using (var ambito1 = _servicios.CreateScope())
        {
            var pagina1 = CrearRestablecer(ambito1.ServiceProvider, usuario.Id, codigo, cerrojo1);
            InvocarSync(pagina1, "OnInitialized");
            await InvocarAsync(pagina1, "OnInitializedAsync");
            RellenarEntrada(pagina1, ContrasenaNuevaPrimerIntento);
            await InvocarAsync(pagina1, "GuardarAsync");

            LeerCampo<bool>(pagina1, "_enlaceInvalido").Should().BeFalse(
                "el primer intento con un token recién emitido tiene que tener éxito");
            LeerCampo<bool>(pagina1, "_completado").Should().BeTrue();
        }
        cerrojo1.ClaveRecibida.Should().Be($"credencial:{usuario.Id}",
            "la clave compartida con CambiarContrasena — y explícitamente NO con verificar-2fa:{userId}");

        string securityStampTrasExito;
        using (var ambitoVerificacion1 = _servicios.CreateScope())
        {
            var userManagerVerificacion = ambitoVerificacion1.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var usuarioTrasExito = await userManagerVerificacion.FindByIdAsync(usuario.Id.ToString());
            (await userManagerVerificacion.CheckPasswordAsync(usuarioTrasExito!, ContrasenaNuevaPrimerIntento)).Should().BeTrue(
                "ResetPasswordAsync tuvo éxito: la contraseña nueva tiene que verificar contra Identity real, releída desde una BD nueva");
            usuarioTrasExito!.DebeCambiarContrasena.Should().BeFalse(
                "CONSOLIDACIÓN: la bandera se fija ANTES de ResetPasswordAsync sobre la misma instancia de " +
                "_usuario, así que Identity la persiste en la MISMA actualización que la contraseña, sin un " +
                "UpdateAsync separado. Sensible a la regresión que quita ese adelanto: ver el comentario de la clase.");
            securityStampTrasExito = usuarioTrasExito.SecurityStamp!;
        }

        // Segundo "request": el MISMO código, ámbito nuevo — como una segunda
        // pestaña que llegó con el mismo enlace de correo. El SecurityStamp ya
        // rotó en el primer éxito, así que el token embebido deja de
        // verificar. Esto es lo que convierte la hipótesis de "token de un
        // solo uso" de Identity 10.0.11 en FACT.
        using (var ambito2 = _servicios.CreateScope())
        {
            var pagina2 = CrearRestablecer(ambito2.ServiceProvider, usuario.Id, codigo);
            InvocarSync(pagina2, "OnInitialized");
            await InvocarAsync(pagina2, "OnInitializedAsync");
            RellenarEntrada(pagina2, ContrasenaNuevaSegundoIntento);
            await InvocarAsync(pagina2, "GuardarAsync");

            LeerCampo<bool>(pagina2, "_enlaceInvalido").Should().BeTrue(
                "el segundo intento con el token ya consumido tiene que caer en el camino de enlace inválido " +
                "— ese camino en producción solo se toma cuando el código de Identity es \"InvalidToken\"");
            LeerCampo<bool>(pagina2, "_completado").Should().BeFalse();
        }

        // Prueba directa del código EXACTO de Identity, sin pasar por la
        // página (que no expone el código, solo la decisión que toma con él).
        using (var ambitoCodigo = _servicios.CreateScope())
        {
            var userManagerCodigo = ambitoCodigo.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var usuarioParaCodigo = await userManagerCodigo.FindByIdAsync(usuario.Id.ToString());
            var resultadoDirecto = await userManagerCodigo.ResetPasswordAsync(usuarioParaCodigo!, token, "OtraMas123!");
            resultadoDirecto.Succeeded.Should().BeFalse();
            resultadoDirecto.Errors.Should().Contain(e => e.Code == "InvalidToken",
                "el mismo token, reutilizado tras un reset exitoso que rotó el SecurityStamp, tiene que fallar " +
                "con este código exacto de IdentityErrorDescriber — la FACT que sostiene _enlaceInvalido");
        }

        using var ambitoVerificacion2 = _servicios.CreateScope();
        var userManagerFinal = ambitoVerificacion2.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var usuarioFinal = await userManagerFinal.FindByIdAsync(usuario.Id.ToString());
        (await userManagerFinal.CheckPasswordAsync(usuarioFinal!, ContrasenaNuevaPrimerIntento)).Should().BeTrue(
            "ningún intento fallido posterior puede haber cambiado la contraseña que dejó el primer éxito");
        (await userManagerFinal.CheckPasswordAsync(usuarioFinal!, ContrasenaNuevaSegundoIntento)).Should().BeFalse();
        usuarioFinal!.SecurityStamp.Should().Be(securityStampTrasExito,
            "un intento rechazado por InvalidToken no llega a persistir nada: el SecurityStamp no puede rotar de nuevo");
        usuarioFinal.DebeCambiarContrasena.Should().BeFalse();
    }

    /// <summary>
    /// Mismo patrón que el test de arriba, para <c>CambiarContrasena.razor</c>:
    /// dos cambios seguidos con la MISMA "contraseña actual" (la que dejó de
    /// serlo tras el primer éxito). <c>ChangePasswordAsync</c> rechaza el
    /// segundo con <c>PasswordMismatch</c> sin ayuda de ningún cerrojo — el
    /// cerrojo compartido con <c>RestablecerContrasena</c> protege el doble
    /// envío desde dos pestañas simultáneas (probado en
    /// <c>EleccionLiderPostgresServiceTests</c>), no este caso secuencial.
    ///
    /// <para>
    /// <b>Sensibilidad de la aserción de consolidación</b> (verificada a mano):
    /// quitar «<c>usuario.DebeCambiarContrasena = false;</c>» de
    /// <c>CambiarContrasena.razor</c> (manteniendo retirado el segundo
    /// <c>UpdateAsync</c>) hace que <c>DebeCambiarContrasena.Should().BeFalse()</c>
    /// de abajo falle con <c>true</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Cambiar_dos_veces_seguidas_con_la_misma_contrasena_actual_la_segunda_falla_por_PasswordMismatch()
    {
        const string ContrasenaActual = "Passw0rd!2024";
        const string ContrasenaNuevaPrimerIntento = "Nueva12345!";
        const string ContrasenaNuevaSegundoIntento = "OtraNueva99!";

        using var ambitoSiembra = _servicios.CreateScope();
        var userManagerSiembra = ambitoSiembra.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var usuario = new ApplicationUser
        {
            UserName = $"{Guid.NewGuid():N}@x.test",
            Email = $"{Guid.NewGuid():N}@x.test",
            NombreCompleto = "Usuario de prueba",
            TenantId = Guid.NewGuid(),
            DebeCambiarContrasena = true,
        };
        (await userManagerSiembra.CreateAsync(usuario, ContrasenaActual)).Succeeded.Should().BeTrue();

        // Primer "request": cambio correcto con la contraseña actual real.
        var cerrojo1 = new CerrojoSiempreConcedidoFalso();
        using (var ambito1 = _servicios.CreateScope())
        {
            var pagina1 = CrearCambiar(ambito1.ServiceProvider, usuario.Id, cerrojo1);
            InvocarSync(pagina1, "OnInitialized");
            RellenarEntradaCambio(pagina1, ContrasenaActual, ContrasenaNuevaPrimerIntento);
            await InvocarAsync(pagina1, "CambiarContrasenaAsync");

            LeerCampo<string?>(pagina1, "mensajeError").Should().BeNull(
                "el primer cambio, con la contraseña actual real, tiene que tener éxito");
        }
        cerrojo1.ClaveRecibida.Should().Be($"credencial:{usuario.Id}",
            "la clave compartida con RestablecerContrasena — y explícitamente NO con verificar-2fa:{userId}");

        string securityStampTrasExito;
        using (var ambitoVerificacion1 = _servicios.CreateScope())
        {
            var userManagerVerificacion = ambitoVerificacion1.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var usuarioTrasExito = await userManagerVerificacion.FindByIdAsync(usuario.Id.ToString());
            (await userManagerVerificacion.CheckPasswordAsync(usuarioTrasExito!, ContrasenaNuevaPrimerIntento)).Should().BeTrue(
                "ChangePasswordAsync tuvo éxito: la contraseña nueva tiene que verificar contra Identity real, releída desde una BD nueva");
            usuarioTrasExito!.DebeCambiarContrasena.Should().BeFalse(
                "CONSOLIDACIÓN: mismo razonamiento que en RestablecerContrasena — la bandera se fija ANTES de " +
                "ChangePasswordAsync sobre la misma instancia, así que Identity la persiste en la MISMA " +
                "actualización que la contraseña.");
            securityStampTrasExito = usuarioTrasExito.SecurityStamp!;
        }

        // Segundo "request": la MISMA "contraseña actual" que el primer
        // cambio ya invalidó.
        using (var ambito2 = _servicios.CreateScope())
        {
            var pagina2 = CrearCambiar(ambito2.ServiceProvider, usuario.Id);
            InvocarSync(pagina2, "OnInitialized");
            RellenarEntradaCambio(pagina2, ContrasenaActual, ContrasenaNuevaSegundoIntento);
            await InvocarAsync(pagina2, "CambiarContrasenaAsync");

            LeerCampo<string?>(pagina2, "mensajeError").Should().NotBeNull(
                "la \"contraseña actual\" del segundo intento ya no es la contraseña real de la cuenta");
        }

        // Prueba directa del código EXACTO de Identity, sin pasar por la
        // página (que no expone el código, solo la descripción).
        using (var ambitoCodigo = _servicios.CreateScope())
        {
            var userManagerCodigo = ambitoCodigo.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var usuarioParaCodigo = await userManagerCodigo.FindByIdAsync(usuario.Id.ToString());
            var resultadoDirecto = await userManagerCodigo.ChangePasswordAsync(usuarioParaCodigo!, ContrasenaActual, "OtraMas123!");
            resultadoDirecto.Succeeded.Should().BeFalse();
            resultadoDirecto.Errors.Should().Contain(e => e.Code == "PasswordMismatch",
                "la contraseña \"actual\" ya dejó de serlo tras el primer cambio exitoso — este es el código " +
                "exacto de IdentityErrorDescriber para ese rechazo");
        }

        using var ambitoVerificacion2 = _servicios.CreateScope();
        var userManagerFinal = ambitoVerificacion2.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var usuarioFinal = await userManagerFinal.FindByIdAsync(usuario.Id.ToString());
        (await userManagerFinal.CheckPasswordAsync(usuarioFinal!, ContrasenaNuevaPrimerIntento)).Should().BeTrue(
            "el segundo intento (rechazado) no puede haber cambiado la contraseña que dejó el primero");
        (await userManagerFinal.CheckPasswordAsync(usuarioFinal!, ContrasenaNuevaSegundoIntento)).Should().BeFalse();
        usuarioFinal!.SecurityStamp.Should().Be(securityStampTrasExito,
            "un intento rechazado por PasswordMismatch no llega a persistir nada: el SecurityStamp no puede rotar de nuevo");
        usuarioFinal.DebeCambiarContrasena.Should().BeFalse();
    }

    private static async Task<ApplicationUser> CrearUsuarioAsync(IServiceProvider servicios)
    {
        var userManager = servicios.GetRequiredService<UserManager<ApplicationUser>>();
        var usuario = new ApplicationUser
        {
            UserName = $"{Guid.NewGuid():N}@x.test",
            Email = $"{Guid.NewGuid():N}@x.test",
            NombreCompleto = "Usuario de prueba",
            TenantId = Guid.NewGuid(),
            DebeCambiarContrasena = true,
        };
        (await userManager.CreateAsync(usuario)).Succeeded.Should().BeTrue();
        return usuario;
    }

    private static RestablecerContrasena CrearRestablecer(
        IServiceProvider servicios, Guid userId, string codigo, CerrojoSiempreConcedidoFalso? cerrojo = null)
    {
        var usuarios = servicios.GetRequiredService<UserManager<ApplicationUser>>();
        var pagina = new RestablecerContrasena();
        EscribirPropiedad(pagina, "UserManager", usuarios);
        EscribirPropiedad(pagina, "SignInManager", new SignInManagerFalso(usuarios));
        EscribirPropiedad(pagina, "OpcionesIdentity", Opciones.Create(new IdentityOptions()));
        EscribirPropiedad(pagina, "LoggerFactory", NullLoggerFactory.Instance);
        EscribirPropiedad(pagina, "CerrojoCredencial", cerrojo ?? new CerrojoSiempreConcedidoFalso());
        EscribirPropiedad(pagina, "UserId", userId.ToString());
        EscribirPropiedad(pagina, "Code", codigo);
        return pagina;
    }

    private static CambiarContrasena CrearCambiar(
        IServiceProvider servicios, Guid userId, CerrojoSiempreConcedidoFalso? cerrojo = null)
    {
        var usuarios = servicios.GetRequiredService<UserManager<ApplicationUser>>();
        var pagina = new CambiarContrasena();
        EscribirPropiedad(pagina, "UserManager", usuarios);
        EscribirPropiedad(pagina, "SignInManager", new SignInManagerFalso(usuarios));
        EscribirPropiedad(pagina, "AuthenticationStateProvider", new AutenticacionFalsa(userId));
        EscribirPropiedad(pagina, "Navigation", new NavigationManagerFalsa());
        EscribirPropiedad(pagina, "OpcionesIdentity", Opciones.Create(new IdentityOptions()));
        EscribirPropiedad(pagina, "LoggerFactory", NullLoggerFactory.Instance);
        EscribirPropiedad(pagina, "CerrojoCredencial", cerrojo ?? new CerrojoSiempreConcedidoFalso());
        return pagina;
    }

    /// <summary>Rellena <c>Entrada</c> (ya creada por <c>OnInitialized</c>) antes de invocar el método bajo prueba.</summary>
    private static void RellenarEntrada(RestablecerContrasena pagina, string contrasenaNueva)
    {
        var entrada = LeerPropiedad(pagina, "Entrada")!;
        entrada.GetType().GetProperty("ContrasenaNueva")!.SetValue(entrada, contrasenaNueva);
        entrada.GetType().GetProperty("ConfirmarContrasenaNueva")!.SetValue(entrada, contrasenaNueva);
    }

    private static void RellenarEntradaCambio(CambiarContrasena pagina, string contrasenaActual, string contrasenaNueva)
    {
        var entrada = LeerPropiedad(pagina, "Entrada")!;
        entrada.GetType().GetProperty("ContrasenaActual")!.SetValue(entrada, contrasenaActual);
        entrada.GetType().GetProperty("ContrasenaNueva")!.SetValue(entrada, contrasenaNueva);
        entrada.GetType().GetProperty("ConfirmarContrasenaNueva")!.SetValue(entrada, contrasenaNueva);
    }

    // ── Reflexión: los métodos y campos bajo prueba son privados a propósito.
    // Invocarlos así ejercita el código de producción tal cual, sin bUnit y
    // sin necesitar un RenderHandle real — ninguno llama a StateHasChanged.
    // Mismo patrón que SelectorTemaGuardadoTrasEscrituraConcurrenteTests.

    private static void EscribirPropiedad(object instancia, string nombre, object? valor) =>
        (instancia.GetType().GetProperty(nombre, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No se encontró la propiedad '{nombre}' en {instancia.GetType().Name}."))
            .SetValue(instancia, valor);

    private static object? LeerPropiedad(object instancia, string nombre) =>
        (instancia.GetType().GetProperty(nombre, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No se encontró la propiedad '{nombre}' en {instancia.GetType().Name}."))
            .GetValue(instancia);

    private static T LeerCampo<T>(object instancia, string nombre) =>
        (T)(instancia.GetType().GetField(nombre, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No se encontró el campo '{nombre}' en {instancia.GetType().Name}."))
            .GetValue(instancia)!;

    private static void InvocarSync(object instancia, string nombre)
    {
        var mi = instancia.GetType().GetMethod(nombre, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No se encontró el método '{nombre}' en {instancia.GetType().Name}.");
        mi.Invoke(instancia, null);
    }

    private static async Task InvocarAsync(object instancia, string nombre)
    {
        var mi = instancia.GetType().GetMethod(nombre, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No se encontró el método '{nombre}' en {instancia.GetType().Name}.");
        await (Task)mi.Invoke(instancia, null)!;
    }

    private sealed class SinTenantActual : ITenantActual
    {
        public Guid? TenantId => null;
    }

    private sealed class AutenticacionFalsa(Guid usuarioId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, usuarioId.ToString())], "prueba"))));
    }

    private sealed class NavigationManagerFalsa : NavigationManager
    {
        public NavigationManagerFalsa() => Initialize("http://localhost/", "http://localhost/");

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
        }
    }

    /// <summary>
    /// Cerrojo de prueba que siempre concede: estas dos pruebas ejercitan
    /// ámbitos (scopes) SECUENCIALES, no concurrentes, así que el cerrojo real
    /// (Postgres, ya probado en <c>EleccionLiderPostgresServiceTests</c>) no
    /// aporta nada aquí — ejecuta el trabajo directamente.
    /// </summary>
    private sealed class CerrojoSiempreConcedidoFalso : IEleccionLiderService
    {
        /// <summary>
        /// Última clave recibida — permite comprobar que la página usa la
        /// clave compartida <c>credencial:{userId}</c>, y no otra (hallazgo
        /// de revisión, Codex: sin esto el test pasa igual con cualquier clave).
        /// </summary>
        public string? ClaveRecibida { get; private set; }

        public async Task<bool> IntentarEjecutarComoLiderAsync(
            string clave, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken)
        {
            ClaveRecibida = clave;
            await trabajo(cancellationToken);
            return true;
        }
    }

    /// <summary>
    /// <see cref="SignInManager{TUser}"/> es una clase concreta; solo se
    /// sustituyen <c>SignOutAsync</c> y <c>RefreshSignInAsync</c>, que en
    /// producción necesitan un <see cref="HttpContext"/> real para
    /// reemitir/borrar la cookie — aquí basta con saber si se llamaron.
    /// </summary>
    private sealed class SignInManagerFalso(UserManager<ApplicationUser> usuarios) : SignInManager<ApplicationUser>(
        usuarios, new HttpContextAccessor(),
        new UserClaimsPrincipalFactory<ApplicationUser>(usuarios, Opciones.Create(new IdentityOptions())),
        Opciones.Create(new IdentityOptions()), NullLogger<SignInManager<ApplicationUser>>.Instance,
        new AuthenticationSchemeProvider(Opciones.Create(new AuthenticationOptions())),
        new DefaultUserConfirmation<ApplicationUser>())
    {
        public bool SesionCerrada { get; private set; }
        public bool SesionReemitida { get; private set; }

        public override Task SignOutAsync()
        {
            SesionCerrada = true;
            return Task.CompletedTask;
        }

        public override Task RefreshSignInAsync(ApplicationUser user)
        {
            SesionReemitida = true;
            return Task.CompletedTask;
        }
    }
}
