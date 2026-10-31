using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// ¿Sigue valiendo este principal? La pregunta de la revalidación del circuito, sin cargar la cuenta.
/// </summary>
public interface IVigenciaSesionCuenta
{
    /// <summary>
    /// <c>true</c> si el sello del principal sigue siendo el de su cuenta, la cuenta está activa y su
    /// identificador de sesión no está revocado. Lo que no se pueda afirmar es <c>false</c>.
    /// </summary>
    Task<bool> SigueVigenteAsync(ClaimsPrincipal principal, CancellationToken ct = default);

    /// <summary>
    /// Lo mismo, devolviendo el principal con el que medir ahora el circuito, o <c>null</c> si ya no vale. Sin
    /// Sesión Privilegiada es el que emitiría la cookie (misma fábrica), con el ajuste vigente del Tenant. Con
    /// ella es el propio <paramref name="principal"/>: rehacerlo exigiría cargar la cuenta bajo
    /// <c>cae_app_soporte</c>, así que ese circuito conserva el plazo con que se conectó hasta recargar.
    /// </summary>
    Task<ClaimsPrincipal?> PrincipalVigenteAsync(ClaimsPrincipal principal, CancellationToken ct = default);
}

/// <summary>
/// Implementación de <see cref="IVigenciaSesionCuenta"/>: la misma pregunta que
/// <see cref="SignInManagerCuentaDesactivada.ValidateSecurityStampAsync(ClaimsPrincipal?)"/>, con dos caminos.
///
/// <list type="bullet">
/// <item><b>Sin Sesión Privilegiada seleccionada:</b> <c>ValidateSecurityStampAsync</c>, sin ningún cambio.</item>
/// <item><b>Con Sesión Privilegiada seleccionada:</b> la conexión adopta <c>cae_app_soporte</c>
/// (<c>TenantRlsConnectionInterceptor</c>), y ese rol no lee las credenciales de la cuenta
/// (ASVS 4.0.3 V4.1.3; migración <c>SoporteSinPasswordHashNiSecurityStamp</c>). El sello y la desactivación los comprueba <c>app_cuenta_firmada_vigente</c>, que solo
/// devuelve un booleano sobre la cuenta que firma el contexto, y la revocación
/// <see cref="IRegistroSesionesUsuario.EstaVigenteAsync"/>. No se omite ninguna de las tres
/// comprobaciones.</item>
/// </list>
///
/// <para>
/// <b>Por qué solo con la selección.</b> Fuera de una petición el <c>HttpContext</c> puede faltar: entonces no hay
/// selección, la conexión no adopta <c>cae_app_soporte</c> y el contexto firmado tampoco lleva usuario. La función
/// respondería <c>false</c> a una sesión válida. Con selección hay por fuerza un principal autenticado en el
/// <c>HttpContext</c> (<c>ClienteActivoSeleccionado</c> liga el token a él), que es el que firma el contexto.
/// </para>
///
/// <para>
/// <b>Actor real.</b> <paramref name="principal"/> es siempre el Actor real: el token de selección no lleva Usuario
/// simulado y nada sustituye <c>NameIdentifier</c>. Si la cuenta del principal y la del contexto no coincidieran,
/// el sello no coincide y la respuesta es <c>false</c>. Nunca se mira a la cuenta simulada.
/// </para>
///
/// <para>
/// No sirve para la cookie: su validador necesita la cuenta para rehacer el principal, y además corre antes de
/// fijarse <c>HttpContext.User</c>, cuando la selección todavía se lee como nula
/// (<c>ClienteActivoSeleccionado.LecturaPrematura</c>) y la conexión no adopta <c>cae_app_soporte</c>.
/// </para>
/// </summary>
public sealed class VigenciaSesionCuenta(
    IClienteActivoSeleccionado clienteActivoSeleccionado,
    SignInManager<ApplicationUser> signInManager,
    CaeManagerDbContext db,
    IRegistroSesionesUsuario sesiones,
    IOptions<IdentityOptions> opciones,
    ILogger<VigenciaSesionCuenta> logger) : IVigenciaSesionCuenta
{
    public async Task<ClaimsPrincipal?> PrincipalVigenteAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada is not null)
            return await SigueVigenteAsync(principal, ct) ? principal : null;

        return await signInManager.ValidateSecurityStampAsync(principal) is { } cuenta
            ? await signInManager.ClaimsFactory.CreateAsync(cuenta)
            : null;
    }

    public async Task<bool> SigueVigenteAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada is null)
            return await signInManager.ValidateSecurityStampAsync(principal) is not null;

        var claims = opciones.Value.ClaimsIdentity;
        if (principal.FindFirst(claims.SecurityStampClaimType)?.Value is not { } sello
            || !Guid.TryParse(principal.FindFirst(claims.UserIdClaimType)?.Value, out var cuentaId))
        {
            logger.LogWarning("Sesión rechazada: el principal de la Sesión Privilegiada no lleva sello o cuenta legibles.");
            return false;
        }

        var cuentaVigente = await db.Database
            .SqlQuery<bool>($"SELECT public.app_cuenta_firmada_vigente({sello}) AS \"Value\"")
            .SingleAsync(ct);
        if (!cuentaVigente)
        {
            logger.LogWarning("Sesión rechazada: la cuenta {UsuarioId} no valida su sello o está desactivada.", cuentaId);
            return false;
        }

        if (SignInManagerCuentaDesactivada.LeerSid(principal) is not { } sid)
        {
            logger.LogWarning("Sesión rechazada: la cookie de la cuenta {UsuarioId} no lleva identificador de sesión.", cuentaId);
            return false;
        }

        if (!await sesiones.EstaVigenteAsync(cuentaId, sid, ct))
        {
            logger.LogWarning("Sesión rechazada: la sesión de la cuenta {UsuarioId} está revocada o no existe.", cuentaId);
            return false;
        }

        return true;
    }
}
