using System.Security.Claims;
using CaeManager.Application.Common;
using Microsoft.AspNetCore.Components.Authorization;

namespace CaeManager.Web.Services;

public class CurrentUserService(AuthenticationStateProvider authenticationStateProvider) : ICurrentUserService
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

    // Fuera de un circuito de Blazor (migraciones/siembra al arrancar, jobs en
    // segundo plano) no hay AuthenticationState — no hay usuario que auditar.
    private async Task<ClaimsPrincipal?> ObtenerUsuarioAsync()
    {
        try
        {
            var estado = await authenticationStateProvider.GetAuthenticationStateAsync();
            return estado.User;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
