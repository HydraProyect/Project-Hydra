using System.Security.Claims;
using CaeManager.Application.Common;
using Microsoft.AspNetCore.Components.Authorization;

namespace CaeManager.Web.Services;

public class CurrentUserService(AuthenticationStateProvider authenticationStateProvider) : ICurrentUserService
{
    public async Task<Guid?> ObtenerUsuarioActualIdAsync()
    {
        // Fuera de un circuito de Blazor (migraciones/siembra al arrancar, jobs en
        // segundo plano) no hay AuthenticationState — no hay usuario que auditar.
        AuthenticationState estado;
        try
        {
            estado = await authenticationStateProvider.GetAuthenticationStateAsync();
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var valorClaim = estado.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return Guid.TryParse(valorClaim, out var usuarioId) ? usuarioId : null;
    }
}
