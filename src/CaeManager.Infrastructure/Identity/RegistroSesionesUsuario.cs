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
    /// SECURITY DEFINER <c>app_sesion_vigente</c>.
    /// </summary>
    Task<bool> EstaVigenteAsync(Guid usuarioId, Guid sid, CancellationToken ct = default);

    /// <summary>Revoca solo la sesión <paramref name="sid"/> de la cuenta. Devuelve si había una viva.</summary>
    Task<bool> RevocarAsync(Guid usuarioId, Guid sid, CancellationToken ct = default);

    /// <summary>
    /// Cierra todas las sesiones de la cuenta: rota el sello de seguridad (también corta las
    /// cookies anteriores a los identificadores de sesión y el resto de lo que depende del sello)
    /// y revoca todas sus sesiones vivas. Devuelve cuántas revocó.
    /// </summary>
    Task<int> CerrarTodasAsync(Guid usuarioId, CancellationToken ct = default);
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
/// con su Tenant, así que funciona también desde un Delegated Workspace; la RLS
/// (<c>solo_la_propia_cuenta_revoca</c>) exige además que la cuenta sea quien firma el contexto.
/// Quien revoca desde una petición con una Sesión Privilegiada seleccionada tiene que resolver este
/// servicio en un ámbito sin esa selección (ver <c>CierreSesionEnServidor</c>): con ella, la
/// conexión adopta el rol de solo lectura de soporte.
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
            var sesion = await db.SesionesUsuario
                .SingleOrDefaultAsync(s => s.UsuarioId == usuarioId && s.Sid == sid && s.RevocadaEnUtc == null, ct);
            if (sesion is null)
                return false;
            sesion.Revocar(DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return true;
        }, valorSinCuenta: false);

    public Task<int> CerrarTodasAsync(Guid usuarioId, CancellationToken ct = default) =>
        EnElTenantDeLaCuentaAsync(usuarioId, async cuenta =>
        {
            // Primero el sello: aunque fallara lo siguiente, las cookies ya caen en la siguiente
            // revalidación. Como «No he sido yo» (VigilanciaInicioSesionService).
            var rotado = await userManager.UpdateSecurityStampAsync(cuenta);
            if (!rotado.Succeeded)
                throw new InvalidOperationException($"No se pudo rotar el sello de seguridad de la cuenta {cuenta.Id}.");

            var ahora = DateTime.UtcNow;
            var vivas = await db.SesionesUsuario
                .Where(s => s.UsuarioId == usuarioId && s.RevocadaEnUtc == null)
                .ToListAsync(ct);
            foreach (var sesion in vivas)
                sesion.Revocar(ahora);
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Cerradas todas las sesiones de la cuenta {UsuarioId}: sello rotado y {Sesiones} sesiones revocadas.",
                usuarioId, vivas.Count);
            return vivas.Count;
        }, valorSinCuenta: 0);

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
