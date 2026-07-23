using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Components.Authorization;

namespace CaeManager.Web.Services;

/// <summary>
/// Resuelve el tenant de la sesión Blazor desde el claim <c>tenant_id</c>
/// (ver <see cref="TenantClaimsPrincipalFactory"/> y docs/MULTITENANCY.md
/// § 8). Mismo patrón que <see cref="CurrentUserService"/>: fuera de un
/// circuito de Blazor (migraciones/siembra al arrancar) no hay
/// <c>AuthenticationState</c>, y se resuelve a <c>null</c> — el filtro
/// global interpreta eso como "sin tenant, sin datos" (fallo cerrado).
///
/// <see cref="ITenantActual.TenantId"/> es síncrono porque EF Core necesita
/// evaluarlo dentro de un <c>HasQueryFilter</c>; <c>AuthenticationStateProvider
/// .GetAuthenticationStateAsync()</c> en Blazor Server ya devuelve una tarea
/// completada de inmediato (el <c>ClaimsPrincipal</c> quedó materializado en
/// memoria al negociar el circuito, sin I/O adicional) — resolverla una única
/// vez por instancia (scoped, cacheada en <see cref="_resuelto"/>) y
/// bloquear sobre ella aquí es seguro y no bloquea sobre I/O real.
/// </summary>
public class TenantActual(AuthenticationStateProvider authenticationStateProvider) : ITenantActual
{
    private Guid? _tenantId;
    private bool _resuelto;

    public Guid? TenantId
    {
        get
        {
            if (!_resuelto)
            {
                _tenantId = ResolverAsync().GetAwaiter().GetResult();
                _resuelto = true;
            }

            return _tenantId;
        }
    }

    private async Task<Guid?> ResolverAsync()
    {
        try
        {
            var estado = await authenticationStateProvider.GetAuthenticationStateAsync();
            var valorClaim = estado.User.FindFirst(TenantClaimsPrincipalFactory.TipoClaimTenantId)?.Value;

            return Guid.TryParse(valorClaim, out var tenantId) ? tenantId : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
