using CaeManager.Application.Tenants;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Autorizacion;

/// <inheritdoc cref="IAdministradorDelTenantPropietario" />
/// <remarks>
/// Dos condiciones, y las dos hacen falta: el usuario pertenece al Tenant <b>y</b> tiene el rol
/// <c>Administrador</c>. Cualquiera de las dos por separado autoriza a la persona equivocada. Es el
/// predicado que aplican también las delegaciones heredadas (ADR-004 § 12.2) y el logo del Tenant.
/// </remarks>
public class AdministradorDelTenantPropietarioEnBase(UserManager<ApplicationUser> userManager)
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
        var usuario = await userManager.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == usuarioId, cancellationToken);
        if (usuario is null) return false;

        // El tenant primero: es la mitad que distingue a quien concede de quien
        // recibe, y evita consultar roles de un usuario que ya sabemos que no
        // pinta nada aquí.
        if (usuario.TenantId != tenantId) return false;

        // Sin esto, una cuenta desactivada podía seguir autorizando
        // durante la ventana de gracia de EstaDesactivada (cookie/token ya
        // emitidos antes del Desactivar; ver su propio comentario) — hallazgo
        // de la revisión puente del incremento 1b.
        if (usuario.EstaDesactivada(DateTimeOffset.UtcNow)) return false;

        // GetRolesAsync consulta la BD (join de afiliaciones y roles) sin pasar por el seguimiento.
        var roles = await userManager.GetRolesAsync(usuario);
        return roles.Contains(Roles.Administrador, StringComparer.OrdinalIgnoreCase);
    }
}
