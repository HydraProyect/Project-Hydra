using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Components.Account;

public static class IdentityEndpointsExtensions
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/cuenta/cerrar-sesion", async (SignInManager<ApplicationUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return Results.LocalRedirect("/cuenta/iniciar-sesion");
        });

        return endpoints;
    }
}
