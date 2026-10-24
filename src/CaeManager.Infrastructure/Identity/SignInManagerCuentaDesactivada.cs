using System.Security.Claims;
using CaeManager.Domain.Seguridad;
using CaeManager.Infrastructure.Seguridad;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// <c>SignInManager</c> que hace que <b>desactivar una cuenta corte sus
/// sesiones</b>, no solo el inicio de sesión nuevo con contraseña.
///
/// <para>
/// Antes, «Desactivar» solo escribía <c>LockoutEnd = MaxValue</c>: impedía
/// entrar de nuevo, pero la cookie ya emitida (14 días deslizante) y el
/// circuito de Blazor ya conectado seguían leyendo, exportando y escribiendo,
/// porque el validador del security stamp compara únicamente el stamp
/// (auditoría de seguridad 2026-09-20). Este tipo reúne los dos puntos únicos
/// por los que pasa toda sesión de cookie:
/// </para>
///
/// <list type="bullet">
/// <item><b>Validación</b> — <see cref="ValidateSecurityStampAsync(ApplicationUser?, string?)"/>
/// la usan tanto el <c>SecurityStampValidator</c> de la cookie como el
/// proveedor de autenticación del circuito
/// (<c>ProveedorAutenticacionRevalidada</c>): una cuenta desactivada deja de
/// validar aunque su stamp coincida (cubre también cuentas desactivadas antes
/// de rotar el stamp al desactivar).</item>
/// <item><b>Emisión</b> — <see cref="SignInWithClaimsAsync(ApplicationUser, AuthenticationProperties?, IEnumerable{Claim})"/>
/// es a donde llegan <c>SignInAsync</c>, <c>RefreshSignInAsync</c> y el
/// callback de Microsoft (que no comprueba bloqueo). Sin este freno, una
/// cuenta desactivada con cookie aún sin revalidar cambiaba su contraseña,
/// <c>RefreshSignInAsync</c> le emitía una cookie nueva con el stamp nuevo, y
/// repetirlo cada menos de un intervalo la mantenía dentro para siempre.</item>
/// </list>
///
/// No toca autorización de datos, RLS ni roles: solo decide si hay o no un
/// principal autenticado. Sin principal, <c>TenantActual.TenantId</c> es nulo
/// y el filtro global de EF deniega, como con cualquier petición anónima.
/// </summary>
public class SignInManagerCuentaDesactivada(
    UserManager<ApplicationUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation,
    IVigilanciaInicioSesion vigilancia)
    : SignInManager<ApplicationUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    /// <summary>
    /// Punto de entrada de la validación periódica de la cookie y del circuito: busca
    /// la cuenta por el <c>Id</c> del principal. En el <c>SecurityStampValidator</c>
    /// de la cookie todavía no hay <c>HttpContext.User</c> y por tanto tampoco Tenant,
    /// y <c>AspNetUsers</c> tiene RLS (P1-M1): sin
    /// <see cref="AmbitoIdentificacionSinTenant"/> la cuenta no se encontraría y la
    /// sesión moriría en el primer refresco. Con Tenant en el contexto (el circuito
    /// ya autenticado) el ámbito no cambia nada: la cuenta se lee por la rama de la
    /// propia cuenta de la política.
    /// </summary>
    public override async Task<ApplicationUser?> ValidateSecurityStampAsync(ClaimsPrincipal? principal)
    {
        using var identificacion = AmbitoIdentificacionSinTenant.Abrir();
        return await base.ValidateSecurityStampAsync(principal);
    }

    public override async Task<bool> ValidateSecurityStampAsync(ApplicationUser? user, string? securityStamp)
    {
        if (user is not null && user.EstaDesactivada(DateTimeOffset.UtcNow))
        {
            Logger.LogWarning("Sesión rechazada: la cuenta {UsuarioId} está desactivada.", user.Id);
            return false;
        }

        return await base.ValidateSecurityStampAsync(user, securityStamp);
    }

    public override Task SignInWithClaimsAsync(
        ApplicationUser user, AuthenticationProperties? authenticationProperties, IEnumerable<Claim> additionalClaims)
    {
        if (user.EstaDesactivada(DateTimeOffset.UtcNow))
        {
            // No se lanza: quien llega aquí desde CambiarContrasena o desde el
            // 2FA solo quería refrescar una cookie, y la cuenta desactivada ya
            // no debe tenerla; la cookie vieja muere en la siguiente validación.
            Logger.LogWarning("No se emite sesión: la cuenta {UsuarioId} está desactivada.", user.Id);
            return Task.CompletedTask;
        }

        return base.SignInWithClaimsAsync(user, authenticationProperties, additionalClaims);
    }

    // ── Registro de inicios de sesión y bloqueo por país ─────────────────────
    //
    // Los pasos con credencial de Identity (contraseña y segundo factor) pasan
    // por aquí, aciertan o fallen: registrarlos en este tipo, y no en cada
    // página, hace que una página de login nueva no se quede fuera por olvido.
    // El callback de Microsoft no usa estos métodos y registra por su cuenta
    // (IdentityEndpointsExtensions). Bloqueo por país: CanSignInAsync, que
    // Identity consulta antes de comprobar la contraseña y otra vez en el
    // segundo factor; un rechazo sale como IsNotAllowed, con el mismo mensaje
    // genérico que cualquier otro fallo.

    private bool _cuentaEncontrada;
    private bool _paisNoPermitido;

    public override async Task<bool> CanSignInAsync(ApplicationUser user)
    {
        if (!await base.CanSignInAsync(user))
            return false;
        if (await vigilancia.PermiteOrigenAsync(user.Id, user.TenantId))
            return true;
        _paisNoPermitido = true;
        return false;
    }

    public override async Task<SignInResult> PasswordSignInAsync(string userName, string password, bool isPersistent, bool lockoutOnFailure)
    {
        _cuentaEncontrada = false;
        var resultado = await base.PasswordSignInAsync(userName, password, isPersistent, lockoutOnFailure);
        // Cuenta inexistente: la base devolvió Failed sin llegar a la
        // sobrecarga con usuario. Solo cuenta para la ráfaga por IP.
        if (!_cuentaEncontrada)
            await vigilancia.RegistrarAsync(null, null, MetodoInicioSesion.Contrasena, ResultadoInicioSesion.Fallido);
        return resultado;
    }

    public override async Task<SignInResult> PasswordSignInAsync(ApplicationUser user, string password, bool isPersistent, bool lockoutOnFailure)
    {
        _cuentaEncontrada = true;
        _paisNoPermitido = false;
        var resultado = await base.PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure);
        await RegistrarAsync(user, MetodoInicioSesion.Contrasena, resultado);
        return resultado;
    }

    public override async Task<SignInResult> TwoFactorAuthenticatorSignInAsync(string code, bool isPersistent, bool rememberClient)
    {
        var usuario = await GetTwoFactorAuthenticationUserAsync();
        _paisNoPermitido = false;
        var resultado = await base.TwoFactorAuthenticatorSignInAsync(code, isPersistent, rememberClient);
        if (usuario is not null)
            await RegistrarAsync(usuario, MetodoInicioSesion.SegundoFactor, resultado);
        return resultado;
    }

    public override async Task<SignInResult> TwoFactorRecoveryCodeSignInAsync(string recoveryCode)
    {
        var usuario = await GetTwoFactorAuthenticationUserAsync();
        _paisNoPermitido = false;
        var resultado = await base.TwoFactorRecoveryCodeSignInAsync(recoveryCode);
        if (usuario is not null)
            await RegistrarAsync(usuario, MetodoInicioSesion.CodigoRecuperacion, resultado);
        return resultado;
    }

    private Task RegistrarAsync(ApplicationUser user, MetodoInicioSesion metodo, SignInResult resultado)
    {
        var registrado =
            resultado.Succeeded ? ResultadoInicioSesion.Correcto
            : resultado.RequiresTwoFactor ? ResultadoInicioSesion.SegundoFactorRequerido
            : resultado.IsLockedOut ? ResultadoInicioSesion.Bloqueado
            : resultado.IsNotAllowed ? ResultadoInicioSesion.Rechazado
            : ResultadoInicioSesion.Fallido;
        var senales = _paisNoPermitido && resultado.IsNotAllowed ? SenalesInicioSesion.PaisNoPermitido : SenalesInicioSesion.Ninguna;
        return vigilancia.RegistrarAsync(user.Id, user.TenantId, metodo, registrado, senales);
    }
}
