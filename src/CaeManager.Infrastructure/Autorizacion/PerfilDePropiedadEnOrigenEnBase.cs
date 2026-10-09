using CaeManager.Application.Tenants;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Autorizacion;

/// <inheritdoc cref="IPerfilDePropiedadEnOrigen" />
/// <remarks>
/// Mismo cuidado que <see cref="AdministradorDelTenantPropietarioEnBase"/>: la cuenta se lee sin
/// seguimiento, se exige que sea miembro del Tenant de origen que afirma la sesión y que no esté
/// desactivada, y los roles salen de la base. Una cuenta con más de un rol es un estado que no
/// debería existir (un cambio de rol a medias): no eleva.
/// </remarks>
public class PerfilDePropiedadEnOrigenEnBase(UserManager<ApplicationUser> userManager)
    : IPerfilDePropiedadEnOrigen
{
    public async Task<string?> ObtenerAsync(
        Guid usuarioId, Guid tenantOrigenId, CancellationToken cancellationToken = default)
    {
        if (usuarioId == Guid.Empty || tenantOrigenId == Guid.Empty) return null;

        // Sin seguimiento: con un circuito de Blazor de larga vida, la instancia rastreada
        // conservaría un rol ya revocado o una cuenta ya desactivada.
        var usuario = await userManager.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == usuarioId, cancellationToken);
        if (usuario is null) return null;

        // El Tenant de origen llega de la claim de sesión; se contrasta con la cuenta.
        if (usuario.TenantId != tenantOrigenId) return null;

        if (usuario.EstaDesactivada(DateTimeOffset.UtcNow)) return null;

        var roles = await userManager.GetRolesAsync(usuario);
        if (roles.Count != 1) return null;

        if (string.Equals(roles[0], Roles.Administrador, StringComparison.Ordinal)) return Roles.Administrador;
        if (string.Equals(roles[0], Roles.DireccionCae, StringComparison.Ordinal)) return Roles.DireccionCae;
        return null;
    }
}
