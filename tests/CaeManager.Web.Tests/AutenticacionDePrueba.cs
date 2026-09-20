using System.Security.Claims;
using Bunit;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Los disparadores de escritura de las páginas viven dentro de <c>SoloConEscritura</c>
/// (un <c>AuthorizeView</c>), que necesita un estado de autenticación y un servicio de
/// autorización. Los tests que montan una página para probar otra cosa —no el permiso—
/// se identifican como un rol con escritura, igual que lo haría un Gestor CAE real; los
/// que sí prueban el permiso (<c>SoloLecturaEnLaInterfazTests</c>) eligen su rol.
/// </summary>
internal static class AutenticacionDePrueba
{
    public static void ConRolDeEscritura(this BunitContext ctx, string rol = Roles.GestorCae)
    {
        ctx.Services.AddScoped<AuthenticationStateProvider>(_ => new Autenticacion(rol));
        ctx.Services.AddAuthorizationCore();
        ctx.Services.AddScoped<IAuthorizationService, AutorizacionPorRoles>();
        ctx.Services.AddCascadingAuthenticationState();
    }

    private sealed class Autenticacion(string rol) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "test"))));
    }

    private sealed class AutorizacionPorRoles : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
        {
            var cumple = requirements.All(r => r switch
            {
                RolesAuthorizationRequirement roles => roles.AllowedRoles.Any(user.IsInRole),
                DenyAnonymousAuthorizationRequirement => user.Identity?.IsAuthenticated == true,
                _ => true
            });
            return Task.FromResult(cumple ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(AuthorizationResult.Success());
    }
}
