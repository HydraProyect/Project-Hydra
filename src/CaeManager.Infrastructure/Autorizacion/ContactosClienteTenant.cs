using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Autorizacion;

/// <summary>
/// Emails de los usuarios de portal (rol Cliente) de un Cliente concreto —
/// destinatarios de ReclamacionDocumental. AspNetUsers es la única tabla sin
/// filtro global (ver DirectorioUsuariosTenant): filtra explícitamente por
/// tenant activo para no exponer el contacto de un Cliente de otro tenant.
/// </summary>
public class ContactosClienteTenant(UserManager<ApplicationUser> userManager, ITenantActual tenantActual)
    : IContactosClienteService
{
    public async Task<IReadOnlyList<string>> ObtenerEmailsPortalAsync(Guid clienteId, CancellationToken cancellationToken = default)
    {
        if (tenantActual.TenantId is not { } tenantId) return [];

        return await userManager.Users
            .Where(u => u.TenantId == tenantId && u.ClienteId == clienteId && u.Email != null)
            .Select(u => u.Email!)
            .Distinct()
            .ToListAsync(cancellationToken);
    }
}
