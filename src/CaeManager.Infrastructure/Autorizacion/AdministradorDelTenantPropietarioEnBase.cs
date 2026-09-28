using CaeManager.Application.Tenants;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

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

        var usuario = await userManager.FindByIdAsync(usuarioId.ToString());
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

        return await userManager.IsInRoleAsync(usuario, Roles.Administrador);
    }
}
