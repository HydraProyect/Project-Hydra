using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Components.Layout;

public partial class MainLayout
{
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;
    [Inject] private ActividadUsuarioService ActividadUsuario { get; set; } = default!;

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
    /// el Layout. Las únicas pantallas que nunca pasan por aquí son
    /// CambiarContrasena.razor y ConfigurarAutenticadorDosFactores.razor,
    /// porque usan AuthLayout en vez de este Layout — no hace falta
    /// comprobar la ruta actual a mano, ni hay bucle de redirección con el
    /// guard de 2FA de más abajo.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        var estadoAutenticacion = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        if (estadoAutenticacion.User.Identity?.IsAuthenticated != true) return;

        var idClaim = estadoAutenticacion.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(idClaim, out var id)) return;

        // Se resuelve una única vez por circuito (ActividadUsuarioService) — el resultado
        // no se usa aquí, solo se dispara para que ya esté cacheado cuando el Home lo pida.
        _ = await ActividadUsuario.RegistrarYEvaluarAsync(RendererInfo.IsInteractive);

        // Por la puerta: este guard corre en paralelo con la inicialización
        // de los demás componentes del layout y de la página, todos sobre el
        // mismo DbContext scoped (ver PuertaAccesoDatos).
        await PuertaAccesoDatos.EjecutarAsync(async () =>
        {
            var usuario = await UserManager.FindByIdAsync(id.ToString());
            if (usuario is null) return;

            if (usuario.DebeCambiarContrasena)
            {
                Navigation.NavigateTo("/cuenta/cambiar-contrasena", forceLoad: true);
                return;
            }

            // Mismo motivo que el guard de arriba: sin esto, un usuario
            // auto-provisionado por SSO sin rol asignado (ver
            // IdentityEndpointsExtensions) podría saltarse la sala de espera
            // escribiendo cualquier otra URL a mano.
            var roles = await UserManager.GetRolesAsync(usuario);
            if (roles.Count == 0)
            {
                Navigation.NavigateTo("/cuenta/pendiente-de-rol", forceLoad: true);
                return;
            }

            // 2FA obligatoria para Administrador (P1-13 de
            // docs/business/MATURITY_REVIEW.md): es el rol con más alcance
            // del sistema, y hoy activar la autenticación en dos pasos era
            // opt-in — nadie la exigía. ConfigurarAutenticadorDosFactores.razor
            // usa AuthLayout, no este Layout, así que no vuelve a pasar por
            // aquí y no hay bucle de redirección.
            if (roles.Contains(Roles.Administrador) && !await UserManager.GetTwoFactorEnabledAsync(usuario))
                Navigation.NavigateTo("/cuenta/configurar-2fa", forceLoad: true);
        });
    }
}
