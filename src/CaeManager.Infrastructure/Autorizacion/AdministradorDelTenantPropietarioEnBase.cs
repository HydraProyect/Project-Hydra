using CaeManager.Application.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Autorizacion;

/// <inheritdoc cref="IAdministradorDelTenantPropietario" />
/// <remarks>
/// Dos condiciones, y las dos hacen falta: el usuario pertenece al Tenant <b>y</b> tiene el rol
/// <c>Administrador</c>. Cualquiera de las dos por separado autoriza a la persona equivocada. Es el
/// predicado que aplican también las delegaciones heredadas (ADR-004 § 12.2) y el logo del Tenant.
/// </remarks>
public class AdministradorDelTenantPropietarioEnBase(CaeManagerDbContext db)
    : IAdministradorDelTenantPropietario
{
    public async Task<bool> EsAdministradorEnBaseAsync(
        Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty) return false;

        // Lectura sin seguimiento: FindByIdAsync devuelve la instancia rastreada por el contexto si ya
        // estaba cargada, y IsInRoleAsync busca la afiliación con Find, que también usa el
        // seguimiento. Con un circuito de Blazor de larga vida eso autoriza con un rol ya revocado o
        // con una cuenta ya desactivada (C6/I7: el rol efectivo se lee de BD en cada decisión).
        // Proyección y no la cuenta entera: bajo Sesión Privilegiada la conexión lleva
        // cae_app_soporte, que no lee PasswordHash ni SecurityStamp.
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
        if (cuenta is null) return false;

        // El tenant primero: es la mitad que distingue a quien concede de quien recibe.
        if (cuenta.TenantId != tenantId) return false;

        // Sin esto, una cuenta desactivada podía seguir autorizando
        // durante la ventana de gracia de EstaDesactivada (cookie/token ya
        // emitidos antes del Desactivar; ver su propio comentario) — hallazgo
        // de la revisión puente del incremento 1b.
        if (ApplicationUser.BloqueoDesactivaLaCuenta(cuenta.LockoutEnd, DateTimeOffset.UtcNow)) return false;

        return cuenta.Roles.Contains(Roles.Administrador, StringComparer.OrdinalIgnoreCase);
    }
}
