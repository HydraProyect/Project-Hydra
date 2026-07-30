using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Components.Authorization;

namespace CaeManager.Web.Services;

public class CurrentUserService(
    AuthenticationStateProvider authenticationStateProvider, IHttpContextAccessor httpContextAccessor) : ICurrentUserService
{
    public async Task<Guid?> ObtenerUsuarioActualIdAsync()
    {
        var usuario = await ObtenerUsuarioAsync();
        if (usuario is null) return null;

        var valorClaim = usuario.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return Guid.TryParse(valorClaim, out var usuarioId) ? usuarioId : null;
    }

    public async Task<string?> ObtenerRolActualAsync()
    {
        var usuario = await ObtenerUsuarioAsync();
        return usuario?.FindFirst(ClaimTypes.Role)?.Value;
    }

    public async Task<Guid?> ObtenerTenantOrigenIdAsync()
    {
        var usuario = await ObtenerUsuarioAsync();
        var valorClaim = usuario?.FindFirst(TenantClaimsPrincipalFactory.TipoClaimTenantId)?.Value;
        return Guid.TryParse(valorClaim, out var tenantId) ? tenantId : null;
    }

    // Dentro de un circuito de Blazor, AuthenticationStateProvider ya trae el
    // ClaimsPrincipal correcto (capturado al negociar el circuito). Fuera de
    // uno — endpoints minimal API como GET /documentos/{id}/archivo, que no
    // tienen circuito pero sí HttpContext.User ya autenticado por la cookie
    // de Identity — hace falta el fallback a IHttpContextAccessor; si tampoco
    // hay HttpContext (migraciones/siembra al arrancar, jobs en segundo
    // plano), no hay usuario que auditar.
    private async Task<ClaimsPrincipal?> ObtenerUsuarioAsync()
    {
        try
        {
            var estado = await authenticationStateProvider.GetAuthenticationStateAsync();
            if (estado.User.Identity?.IsAuthenticated == true)
                return estado.User;
        }
        catch (InvalidOperationException)
        {
            // sin circuito de Blazor — se intenta el fallback de abajo.
        }

        var usuarioHttp = httpContextAccessor.HttpContext?.User;
        return usuarioHttp?.Identity?.IsAuthenticated == true ? usuarioHttp : null;
    }
}
