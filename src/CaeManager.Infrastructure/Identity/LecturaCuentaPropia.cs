using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Lo que la sesión necesita saber de su propia cuenta, sin credenciales (ASVS 4.0.3 V4.1.3).
/// </summary>
/// <param name="DebeCambiarContrasena">La cuenta tiene pendiente cambiar la contraseña.</param>
/// <param name="DobleFactorActivo">La cuenta tiene activo el segundo factor.</param>
/// <param name="Roles">Los roles de Identity de la cuenta, en el orden en que los devuelve la base.</param>
public sealed record EstadoCuentaPropia(bool DebeCambiarContrasena, bool DobleFactorActivo, IReadOnlyList<string> Roles);

/// <summary>
/// Lee <see cref="EstadoCuentaPropia"/> y el rol de origen proyectando columnas, sin materializar
/// <see cref="ApplicationUser"/>: en una Sesión Privilegiada la conexión lleva <c>cae_app_soporte</c>, y una cuenta
/// completa arrastraría también <c>PasswordHash</c> y <c>SecurityStamp</c>. Es solo lectura: el resultado no se guarda
/// ni autoriza nada por sí mismo.
/// </summary>
public interface ILecturaCuentaPropia
{
    /// <summary>
    /// El rol de la cuenta, o <c>null</c> si no tiene ninguno o esta conexión no la ve. Hay uno por usuario
    /// (<c>Usuarios.razor</c>); el orden solo hace determinista un estado que no debería existir.
    /// </summary>
    Task<string?> LeerRolAsync(Guid usuarioId, CancellationToken ct = default);

    /// <summary>El estado de la cuenta, o <c>null</c> si esta conexión no la ve.</summary>
    Task<EstadoCuentaPropia?> ObtenerAsync(Guid usuarioId, CancellationToken ct = default);
}

/// <summary>
/// Implementación de <see cref="ILecturaCuentaPropia"/>. Sin rastreo y en una sola sentencia: un circuito ve la cuenta
/// en fresco aunque otro ámbito acabe de cambiarla, y cuenta y roles salen de la misma instantánea.
/// </summary>
public sealed class LecturaCuentaPropia(CaeManagerDbContext db) : ILecturaCuentaPropia
{
    public async Task<string?> LeerRolAsync(Guid usuarioId, CancellationToken ct = default)
    {
        var roles = await (from cuenta in db.Users.AsNoTracking()
                           where cuenta.Id == usuarioId
                           join asignacion in db.UserRoles on cuenta.Id equals asignacion.UserId
                           join rol in db.Roles on asignacion.RoleId equals rol.Id
                           select rol.Name).ToListAsync(ct);
        return roles.OfType<string>().Order(StringComparer.Ordinal).FirstOrDefault();
    }

    public async Task<EstadoCuentaPropia?> ObtenerAsync(Guid usuarioId, CancellationToken ct = default)
    {
        var cuenta = await db.Users.AsNoTracking()
            .Where(u => u.Id == usuarioId)
            .Select(u => new
            {
                u.DebeCambiarContrasena,
                u.TwoFactorEnabled,
                Roles = db.UserRoles
                    .Where(ur => ur.UserId == u.Id)
                    .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name)
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);

        return cuenta is null
            ? null
            : new EstadoCuentaPropia(cuenta.DebeCambiarContrasena, cuenta.TwoFactorEnabled, cuenta.Roles.OfType<string>().ToList());
    }
}
