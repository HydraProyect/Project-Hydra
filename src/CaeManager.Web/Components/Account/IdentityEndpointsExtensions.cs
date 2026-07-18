using System.Security.Claims;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Components.Account;

public static class IdentityEndpointsExtensions
{
    /// <summary>Nombre del esquema de autenticación de Entra ID — usado tanto al registrarlo (Program.cs) como al retarlo (aquí).</summary>
    public const string EsquemaMicrosoft = "Microsoft";

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/cuenta/cerrar-sesion", async (SignInManager<ApplicationUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return Results.LocalRedirect("/cuenta/iniciar-sesion");
        });

        // Punto de entrada del botón "Iniciar sesión con Microsoft" (Login.razor)
        // — solo se muestra si AzureAdOptions.EstaConfigurado. El propio
        // middleware de autenticación intercepta la vuelta desde Microsoft en
        // CallbackPath ("/signin-microsoft", ver Program.cs) antes de que
        // llegue aquí ningún código nuestro; RedirectUri es a dónde se manda
        // al navegador después de que ese middleware ya haya firmado la
        // cookie externa temporal.
        endpoints.MapGet("/cuenta/iniciar-sesion-microsoft", (string? returnUrl) =>
        {
            var destino = "/cuenta/microsoft-callback";
            if (!string.IsNullOrWhiteSpace(returnUrl))
                destino += $"?returnUrl={Uri.EscapeDataString(returnUrl)}";

            var propiedades = new AuthenticationProperties { RedirectUri = destino };
            return Results.Challenge(propiedades, [EsquemaMicrosoft]);
        }).AllowAnonymous();

        // A dónde vuelve el navegador ya con la cookie externa temporal
        // firmada (IdentityConstants.ExternalScheme) tras un login de
        // Microsoft correcto. Dos reglas explícitas, ver
        // RestriccionLoginLocalClaimsTransformation para el resto del diseño:
        // (1) nunca auto-provisiona cuentas nuevas — el email de Microsoft
        // debe coincidir con un ApplicationUser ya dado de alta por un
        // Administrador en /usuarios; (2) no exige DebeCambiarContrasena —
        // ese flujo es solo para la contraseña local, que un usuario SSO
        // puede no usar nunca.
        endpoints.MapGet("/cuenta/microsoft-callback", async (
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            ILogger<Program> logger,
            string? returnUrl) =>
        {
            var infoExterna = await signInManager.GetExternalLoginInfoAsync();
            if (infoExterna is null)
                return Results.LocalRedirect("/cuenta/iniciar-sesion?errorSso=fallo");

            var email = infoExterna.Principal.FindFirstValue(ClaimTypes.Email)
                ?? infoExterna.Principal.FindFirstValue("preferred_username");

            var usuario = email is not null ? await userManager.FindByEmailAsync(email) : null;
            if (usuario is null)
            {
                logger.LogWarning("Login de Microsoft rechazado: {Email} no tiene una cuenta dada de alta en CAE Manager.", email);
                await signInManager.SignOutAsync();
                return Results.LocalRedirect("/cuenta/iniciar-sesion?errorSso=sin-cuenta");
            }

            var yaVinculado = (await userManager.GetLoginsAsync(usuario))
                .Any(l => l.LoginProvider == infoExterna.LoginProvider && l.ProviderKey == infoExterna.ProviderKey);
            if (!yaVinculado)
                await userManager.AddLoginAsync(usuario, infoExterna);

            await signInManager.SignInWithClaimsAsync(usuario, isPersistent: true,
                additionalClaims: [new Claim(RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin, RestriccionLoginLocalClaimsTransformation.MetodoLoginSso)]);

            return Results.LocalRedirect(string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl);
        }).AllowAnonymous();

        return endpoints;
    }
}
