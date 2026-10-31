using CaeManager.Application.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Autorizacion;

/// <inheritdoc cref="IPerfilDePropiedadEnOrigen" />
/// <remarks>
/// Mismo cuidado que <see cref="AdministradorDelTenantPropietarioEnBase"/>: la cuenta se lee sin
/// seguimiento y por proyección, se exige que sea miembro del Tenant de origen que afirma la sesión y
/// que no esté desactivada, y los roles salen de la base. Una cuenta con más de un rol es un estado que
/// no debería existir (un cambio de rol a medias): no eleva.
/// </remarks>
public class PerfilDePropiedadEnOrigenEnBase(CaeManagerDbContext db)
    : IPerfilDePropiedadEnOrigen
{
    public async Task<string?> ObtenerAsync(
        Guid usuarioId, Guid tenantOrigenId, CancellationToken cancellationToken = default)
    {
        if (usuarioId == Guid.Empty || tenantOrigenId == Guid.Empty) return null;

        // Sin seguimiento: con un circuito de Blazor de larga vida, la instancia rastreada
        // conservaría un rol ya revocado o una cuenta ya desactivada. Proyección: bajo Sesión
        // Privilegiada la conexión lleva cae_app_soporte, que no lee PasswordHash ni SecurityStamp.
        var cuenta = await db.Users.AsNoTracking()
            .Where(u => u.Id == usuarioId)
            .Select(u => new
            {
                u.TenantId,
                u.LockoutEnd,
                Roles = db.UserRoles
                    .Where(ur => ur.UserId == u.Id)
                    .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name)
                    .ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (cuenta is null) return null;

        // El Tenant de origen llega de la claim de sesión; se contrasta con la cuenta.
        if (cuenta.TenantId != tenantOrigenId) return null;

        if (ApplicationUser.BloqueoDesactivaLaCuenta(cuenta.LockoutEnd, DateTimeOffset.UtcNow)) return null;

        if (cuenta.Roles.Count != 1) return null;

        if (string.Equals(cuenta.Roles[0], Roles.Administrador, StringComparison.Ordinal)) return Roles.Administrador;
        if (string.Equals(cuenta.Roles[0], Roles.DireccionCae, StringComparison.Ordinal)) return Roles.DireccionCae;
        return null;
    }
}
