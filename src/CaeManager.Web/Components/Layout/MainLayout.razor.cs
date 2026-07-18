using System.Security.Claims;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Components.Layout;

public partial class MainLayout
{
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    /// <summary>
    /// Forzar el cambio de contraseña en el primer login (ver
    /// ApplicationUser.DebeCambiarContrasena) no sirve de nada si basta con
    /// escribir otra URL a mano para saltárselo — por eso el guard no vive
    /// en Login.razor (que solo actúa justo después de autenticar) sino
    /// aquí, en MainLayout, el layout por defecto de cualquier página
    /// autenticada (ver Routes.razor). OnParametersSetAsync, no
    /// OnInitializedAsync, porque @Body es un parámetro en cascada de
    /// LayoutComponentBase que cambia en cada navegación — así el guard se
    /// revisa de nuevo en cada página, no solo la primera vez que se monta
    /// el Layout. La única pantalla que nunca pasa por aquí es
    /// CambiarContrasena.razor, porque usa AuthLayout en vez de este
    /// Layout — no hace falta comprobar la ruta actual a mano.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        var estadoAutenticacion = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        if (estadoAutenticacion.User.Identity?.IsAuthenticated != true) return;

        var idClaim = estadoAutenticacion.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(idClaim, out var id)) return;

        var usuario = await UserManager.FindByIdAsync(id.ToString());
        if (usuario is { DebeCambiarContrasena: true })
            Navigation.NavigateTo("/cuenta/cambiar-contrasena", forceLoad: true);
    }
}
