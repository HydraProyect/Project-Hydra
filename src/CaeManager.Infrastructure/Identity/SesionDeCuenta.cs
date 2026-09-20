using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Qué hace que la sesión de una cuenta siga o deje de seguir siendo válida.
///
/// <para>
/// Desactivar una cuenta (<c>/usuarios</c>) solo escribía <c>LockoutEnd</c>, y
/// ni la cookie ni el circuito de Blazor miraban el bloqueo: una sesión
/// abierta antes de desactivar seguía leyendo, exportando y escribiendo, y el
/// validador de la cookie la renovaba 14 días más (auditoría 2026-09-20). El
/// corte descansa en <b>dos</b> mecanismos independientes, y cada uno cubre lo
/// que el otro no:
/// </para>
/// <list type="bullet">
/// <item><b>El security stamp rota al desactivar.</b> Cualquier sesión emitida
/// antes deja de casar con la cuenta, <b>y no resucita al reactivarla</b>
/// (con solo el bloqueo, quitar el bloqueo devolvía la validez a una cookie
/// vieja). Es lo único que corta el token de la extensión y la cookie de forma
/// permanente.</item>
/// <item><b>El bloqueo se comprueba en cada validación.</b> Cubre una cuenta
/// bloqueada por cualquier vía que no rote el stamp. Efecto secundario
/// asumido: el bloqueo temporal por intentos fallidos
/// (<c>Lockout.DefaultLockoutTimeSpan</c>) también cierra una sesión ya
/// abierta durante su ventana.</item>
/// </list>
/// </summary>
public static class SesionDeCuenta
{
    /// <summary>
    /// Cada cuánto revalida el validador de la cookie contra la base. Es el
    /// techo de cuánto sobrevive una sesión a una desactivación. El valor por
    /// defecto del framework, 30 minutos, dejaba media hora de margen a una
    /// cuenta ya desactivada; a cambio, una lectura de usuario (y su cookie
    /// re-emitida) como máximo una vez por minuto y sesión activa.
    /// </summary>
    public static readonly TimeSpan IntervaloDeValidacion = TimeSpan.FromMinutes(1);

    /// <summary>
    /// La cookie de Identity valida el security stamp con el
    /// <see cref="ISecurityStampValidator"/> registrado. Sustituye el del
    /// framework por <see cref="ValidadorDeSesionDeCuenta"/> (que además mira el
    /// bloqueo) y baja el intervalo de revalidación a
    /// <see cref="IntervaloDeValidacion"/>. <c>Replace</c>, no <c>Add</c>:
    /// <c>AddSignInManager</c> ya registró el del framework y el orden de
    /// registro no debe decidir cuál gana.
    /// </summary>
    public static IServiceCollection AddValidacionDeSesionDeCuenta(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Scoped<ISecurityStampValidator, ValidadorDeSesionDeCuenta>());
        services.Configure<SecurityStampValidatorOptions>(opciones =>
            opciones.ValidationInterval = IntervaloDeValidacion);
        return services;
    }

    /// <summary>
    /// Activa o desactiva la cuenta en una sola escritura. Desactivar rota el
    /// security stamp en la misma <c>UpdateAsync</c> que fija el bloqueo, así
    /// que no existe un instante persistido con la cuenta bloqueada y las
    /// sesiones viejas aún casando. Reactivar quita el bloqueo y <b>no</b>
    /// toca el stamp: el que rotó al desactivar sigue invalidando lo emitido
    /// antes.
    /// </summary>
    public static Task<IdentityResult> CambiarActivacionAsync(
        UserManager<ApplicationUser> userManager, ApplicationUser usuario, bool activar)
    {
        usuario.LockoutEnabled = true;
        if (activar)
        {
            usuario.LockoutEnd = null;
        }
        else
        {
            usuario.LockoutEnd = DateTimeOffset.MaxValue;
            usuario.SecurityStamp = Guid.NewGuid().ToString();
        }

        return userManager.UpdateAsync(usuario);
    }

    /// <summary>
    /// ¿Sigue valiendo esta sesión? Verdadero solo si la cuenta existe, su
    /// security stamp casa con el del principal y no está bloqueada. Lo usa el
    /// circuito de Blazor; el validador de la cookie hace lo mismo a través de
    /// <see cref="ValidadorDeSesionDeCuenta"/>.
    /// </summary>
    public static async Task<bool> SigueVigenteAsync(
        UserManager<ApplicationUser> userManager, IdentityOptions opciones, ClaimsPrincipal principal)
    {
        var usuario = await userManager.GetUserAsync(principal);
        if (usuario is null) return false;

        if (userManager.SupportsUserSecurityStamp)
        {
            var stampDelPrincipal = principal.FindFirstValue(opciones.ClaimsIdentity.SecurityStampClaimType);
            var stampDeLaCuenta = await userManager.GetSecurityStampAsync(usuario);
            if (!string.Equals(stampDelPrincipal, stampDeLaCuenta, StringComparison.Ordinal))
                return false;
        }

        return !await userManager.IsLockedOutAsync(usuario);
    }
}
