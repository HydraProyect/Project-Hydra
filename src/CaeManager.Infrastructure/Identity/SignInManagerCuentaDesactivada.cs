using System.Security.Claims;
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
    IUserConfirmation<ApplicationUser> confirmation)
    : SignInManager<ApplicationUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
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
}
