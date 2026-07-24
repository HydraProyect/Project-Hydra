using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Components.Authorization;

namespace CaeManager.Web.Services;

/// <summary>
/// Resuelve el tenant de la sesión Blazor desde el claim <c>tenant_id</c>
/// (ver <see cref="TenantClaimsPrincipalFactory"/> y docs/MULTITENANCY.md
/// § 8). Mismo patrón que <see cref="CurrentUserService"/>: dentro de un
/// circuito de Blazor, <c>AuthenticationStateProvider</c> ya trae el
/// <c>ClaimsPrincipal</c> correcto. Fuera de uno — endpoints minimal API
/// como <c>GET /documentos/{id}/archivo</c>, que no tienen circuito pero sí
/// <c>HttpContext.User</c> ya autenticado por la cookie de Identity — hace
/// falta el fallback a <see cref="IHttpContextAccessor"/>. Antes de asumir
/// "sin tenant" en cualquiera de los dos casos, consulta primero
/// <see cref="AmbitoTenantExplicito"/> — así la siembra al arrancar
/// (Program.cs) y futuros jobs de fondo pueden operar sin sesión sin que el
/// interceptor los rechace. Solo si no hay ámbito explícito ni usuario
/// autenticado por ninguna de las dos vías se resuelve a <c>null</c> — el
/// filtro global interpreta eso como "sin tenant, sin datos" (fallo cerrado).
///
/// <see cref="ITenantActual.TenantId"/> es síncrono porque EF Core necesita
/// evaluarlo dentro de un <c>HasQueryFilter</c>; tanto
/// <c>AuthenticationStateProvider.GetAuthenticationStateAsync()</c> en
/// Blazor Server como leer <c>IHttpContextAccessor.HttpContext.User</c> son
/// operaciones sin I/O real (el <c>ClaimsPrincipal</c> ya está materializado
/// en memoria en ambos casos) — resolverlo una única vez por instancia
/// (scoped, cacheado en <see cref="_resuelto"/>) y bloquear sobre ello aquí
/// es seguro.
/// </summary>
public class TenantActual(
    AuthenticationStateProvider authenticationStateProvider, IHttpContextAccessor httpContextAccessor) : ITenantActual
{
    private Guid? _tenantId;
    private bool _resuelto;

    public Guid? TenantId
    {
        get
        {
            if (AmbitoTenantExplicito.TenantIdActual is { } tenantIdExplicito)
                return tenantIdExplicito;

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
        var desdeCircuito = await ResolverDesdeCircuitoAsync();
        if (desdeCircuito is { } tenantId)
            return tenantId;

        var usuarioHttp = httpContextAccessor.HttpContext?.User;
        if (usuarioHttp?.Identity?.IsAuthenticated != true)
            return null;

        var valorClaimHttp = usuarioHttp.FindFirst(TenantClaimsPrincipalFactory.TipoClaimTenantId)?.Value;
        return Guid.TryParse(valorClaimHttp, out var tenantIdHttp) ? tenantIdHttp : null;
    }

    private async Task<Guid?> ResolverDesdeCircuitoAsync()
    {
        try
        {
            var estado = await authenticationStateProvider.GetAuthenticationStateAsync();
            if (estado.User.Identity?.IsAuthenticated != true)
                return null;

            var valorClaim = estado.User.FindFirst(TenantClaimsPrincipalFactory.TipoClaimTenantId)?.Value;
            return Guid.TryParse(valorClaim, out var tenantId) ? tenantId : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
