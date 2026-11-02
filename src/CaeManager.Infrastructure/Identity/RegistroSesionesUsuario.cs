using CaeManager.Application.Common;
using CaeManager.Domain.Seguridad;
using CaeManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Sesiones de cookie emitidas, para revocarlas en el servidor (ver <see cref="SesionUsuario"/>).
/// </summary>
public interface IRegistroSesionesUsuario
{
    /// <summary>
    /// Da de alta la sesión nueva <paramref name="sid"/> de la cuenta, en su Tenant propietario.
    /// Lanza si no se guarda: una cookie sin fila moriría en la primera revalidación.
    /// </summary>
    Task RegistrarAsync(Guid usuarioId, Guid tenantPropietarioId, Guid sid, CancellationToken ct = default);

    /// <summary>
    /// ¿Existe la sesión <paramref name="sid"/> de esa cuenta y no está revocada? Se pregunta
    /// antes de conocer el Tenant (validador de la cookie), por eso va por la función
    /// SECURITY DEFINER <c>app_sesion_vigente</c>. Además, en una Sesión Privilegiada la conexión
    /// adopta <c>cae_app_soporte</c>, que no lee <c>Sid</c> (migración <c>SoporteSinSidSesionUsuario</c>):
    /// una consulta directa a la tabla rompería la revalidación de esa sesión.
    /// </summary>
    Task<bool> EstaVigenteAsync(Guid usuarioId, Guid sid, CancellationToken ct = default);

    /// <summary>Revoca solo la sesión <paramref name="sid"/> de la cuenta. Devuelve si había una viva.</summary>
    Task<bool> RevocarAsync(Guid usuarioId, Guid sid, CancellationToken ct = default);

    /// <summary>
    /// Cierra todas las sesiones de la cuenta: rota el sello de seguridad (también corta las
    /// cookies anteriores a los identificadores de sesión y el resto de lo que depende del sello)
    /// y revoca todas sus sesiones vivas. Devuelve cuántas revocó. No depende de quién firme el
    /// contexto: sirve también al restablecer la contraseña y en «No he sido yo», que llegan por un
    /// enlace de correo, sin sesión de la cuenta. Lanza si no puede: quien llama no debe decir que
    /// las sesiones están cerradas sin que lo estén.
    /// </summary>
    Task<int> CerrarTodasAsync(Guid usuarioId, CancellationToken ct = default);

    /// <summary>
    /// Revoca todas las sesiones vivas de la cuenta salvo <paramref name="sidConservada"/>, la de quien
    /// acaba de cambiar su contraseña; sin ella, todas. No rota el sello: lo ha rotado el propio cambio
    /// de credencial, y esa sesión lo recoge con <c>RefreshSignInAsync</c>. Devuelve cuántas revocó.
    /// Lanza si no puede, como <see cref="CerrarTodasAsync"/>.
    /// </summary>
    Task<int> CerrarLasDemasAsync(Guid usuarioId, Guid? sidConservada, CancellationToken ct = default);
}

/// <summary>
/// Implementación de <see cref="IRegistroSesionesUsuario"/>.
///
/// <para>
/// <b>Tenant.</b> Toda fila vive en el Tenant propietario de la cuenta. El alta llega desde el
/// inicio de sesión, sin sesión ni Tenant en el contexto, con el Tenant de la cuenta que Identity
/// acaba de resolver, como <c>VigilanciaInicioSesionService</c>; va en un ámbito de servicios
/// propio para no guardar nada pendiente del contexto de la petición. Revocar lee la cuenta con
/// <see cref="AmbitoIdentificacionSinTenant"/> y escribe bajo <see cref="AmbitoTenantExplicito"/>
/// con su Tenant, así que funciona también desde un Delegated Workspace. Quien revoca desde una
/// petición con una Sesión Privilegiada seleccionada tiene que resolver este servicio en un ámbito
/// sin esa selección (ver <c>CierreSesionEnServidor</c>): con ella, la conexión adopta el rol de solo
/// lectura de soporte.
/// </para>
///
/// <para>
/// <b>Quién revoca.</b> La RLS (<c>solo_la_propia_cuenta_revoca</c>) exige que la cuenta sea quien
/// firma el contexto, y una sola sesión solo la revoca su cuenta (<see cref="RevocarAsync"/>). Revocar
/// varias tiene que funcionar también sin esa firma: quien restablece la contraseña o confirma «No he
/// sido yo» prueba ser la cuenta con el enlace de su correo, y no tiene sesión de ella o tiene la de
/// otra. Bajo esa política el UPDATE no vería ninguna fila y devolvería 0 sin error, indistinguible de
/// una cuenta sin sesiones. Por eso va por <c>app_revocar_sesiones_de_cuenta</c>, que exige contexto
/// firmado y que su Tenant sea el de la cuenta. Autorizar sigue siendo cosa de quien llama.
/// </para>
/// </summary>
public sealed class RegistroSesionesUsuario(
    CaeManagerDbContext db,
    UserManager<ApplicationUser> userManager,
    IServiceScopeFactory ambitos,
    ILogger<RegistroSesionesUsuario> logger) : IRegistroSesionesUsuario
{
    public async Task RegistrarAsync(Guid usuarioId, Guid tenantPropietarioId, Guid sid, CancellationToken ct = default)
    {
        using var ambito = ambitos.CreateScope();
        using var _ = AmbitoTenantExplicito.Establecer(tenantPropietarioId);
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        contexto.SesionesUsuario.Add(new SesionUsuario(usuarioId, sid, DateTime.UtcNow));
        await contexto.SaveChangesAsync(ct);
    }

    public async Task<bool> EstaVigenteAsync(Guid usuarioId, Guid sid, CancellationToken ct = default) =>
        await db.Database
            .SqlQuery<bool>($"SELECT public.app_sesion_vigente({sid}, {usuarioId}) AS \"Value\"")
            .SingleAsync(ct);

    public Task<bool> RevocarAsync(Guid usuarioId, Guid sid, CancellationToken ct = default) =>
        EnElTenantDeLaCuentaAsync(usuarioId, async _ =>
        {
            // Una sola sentencia con «RevocadaEnUtc IS NULL» en el predicado: dos cierres de la misma
            // sesión a la vez (dos pestañas, un doble clic) no chocan con el disparador
            // sesion_revocada_no_se_reabre. PostgreSQL vuelve a evaluar el predicado sobre la fila que
            // dejó el primero, la ve revocada y el segundo no escribe nada.
            var ahora = DateTime.UtcNow;
            var filas = await db.SesionesUsuario
                .Where(s => s.UsuarioId == usuarioId && s.Sid == sid && s.RevocadaEnUtc == null)
                .ExecuteUpdateAsync(cambios => cambios.SetProperty(s => s.RevocadaEnUtc, ahora), ct);
            return filas > 0;
        }, valorSinCuenta: false);

    public Task<int> CerrarTodasAsync(Guid usuarioId, CancellationToken ct = default) =>
        EnElTenantDeLaCuentaAsync(usuarioId, async cuenta =>
        {
            // Primero el sello: aunque fallara lo siguiente, las cookies ya caen en la siguiente
            // revalidación. Como «No he sido yo» (VigilanciaInicioSesionService).
            var rotado = await userManager.UpdateSecurityStampAsync(cuenta);
            if (!rotado.Succeeded)
                throw new InvalidOperationException($"No se pudo rotar el sello de seguridad de la cuenta {cuenta.Id}.");

            var revocadas = await RevocarVariasAsync(usuarioId, SinSesionConservada, ct);
            logger.LogWarning("Cerradas todas las sesiones de la cuenta {UsuarioId}: sello rotado y {Sesiones} sesiones revocadas.",
                usuarioId, revocadas);
            return revocadas;
        }, valorSinCuenta: 0);

    public Task<int> CerrarLasDemasAsync(Guid usuarioId, Guid? sidConservada, CancellationToken ct = default) =>
        EnElTenantDeLaCuentaAsync(usuarioId, async _ =>
        {
            var revocadas = await RevocarVariasAsync(usuarioId, sidConservada ?? SinSesionConservada, ct);
            logger.LogWarning("Cerradas las demás sesiones de la cuenta {UsuarioId}: {Sesiones} sesiones revocadas.",
                usuarioId, revocadas);
            return revocadas;
        }, valorSinCuenta: 0);

    /// <summary>Ningún identificador de sesión es <see cref="Guid.Empty"/>: ver <c>LeerSid</c>.</summary>
    private static readonly Guid SinSesionConservada = Guid.Empty;

    private async Task<int> RevocarVariasAsync(Guid usuarioId, Guid conservar, CancellationToken ct) =>
        await db.Database
            .SqlQuery<int>($"SELECT public.app_revocar_sesiones_de_cuenta({usuarioId}, {conservar}) AS \"Value\"")
            .SingleAsync(ct);

    private async Task<T> EnElTenantDeLaCuentaAsync<T>(Guid usuarioId, Func<ApplicationUser, Task<T>> accion, T valorSinCuenta)
    {
        ApplicationUser? cuenta;
        using (AmbitoIdentificacionSinTenant.Abrir())
            cuenta = await userManager.FindByIdAsync(usuarioId.ToString());
        if (cuenta is null)
            return valorSinCuenta;

        // El Tenant propietario de la cuenta, no el del workspace que la petición tenga abierto.
        using var _ = AmbitoTenantExplicito.Establecer(cuenta.TenantId);
        return await accion(cuenta);
    }
}
