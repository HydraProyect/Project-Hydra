using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Components.Account;

public static class IdentityEndpointsExtensions
{
    /// <summary>Nombre del esquema de autenticación de Entra ID — usado tanto al registrarlo (Program.cs) como al retarlo (aquí).</summary>
    public const string EsquemaMicrosoft = "Microsoft";

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/cuenta/cerrar-sesion", async (
            SignInManager<ApplicationUser> signInManager, HttpContext httpContext) =>
        {
            await signInManager.SignOutAsync();

            // La cookie de Delegated Workspace no la borra SignOutAsync: es
            // nuestra, no de Identity. Sin esto sobrevivía al cierre de
            // sesión y al volver a entrar se reanudaba el workspace anterior
            // —incluido uno cuya delegación se hubiera revocado entretanto—
            // en lugar de empezar en el tenant de origen (hallazgo N-6 de
            // INFORME-AUDITORIA-2.md).
            httpContext.Response.Cookies.Delete(ClienteActivoSeleccionado.NombreCookie);

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
        // Microsoft correcto. Ver RestriccionLoginLocalClaimsTransformation
        // para el resto del diseño de esta funcionalidad. No exige
        // DebeCambiarContrasena — ese flujo es solo para la contraseña
        // local, que un usuario SSO puede no usar nunca.
        endpoints.MapGet("/cuenta/microsoft-callback", async (
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            IEmailService emailService,
            ILogger<Program> logger,
            string? returnUrl) =>
        {
            var infoExterna = await signInManager.GetExternalLoginInfoAsync();
            if (infoExterna is null)
                return Results.LocalRedirect("/cuenta/iniciar-sesion?errorSso=fallo");

            var email = infoExterna.Principal.FindFirstValue(ClaimTypes.Email)
                ?? infoExterna.Principal.FindFirstValue("preferred_username");

            if (string.IsNullOrWhiteSpace(email))
            {
                logger.LogWarning("Login de Microsoft rechazado: la cuenta no trajo un email utilizable.");
                await signInManager.SignOutAsync();
                return Results.LocalRedirect("/cuenta/iniciar-sesion?errorSso=fallo");
            }

            var usuario = await userManager.FindByEmailAsync(email);
            var esUsuarioNuevo = usuario is null;

            if (usuario is null)
            {
                // Auto-provisión sin rol: cualquier cuenta del tenant de la
                // empresa (ya restringido por AzureAdOptions.TenantId, ver
                // Program.cs) puede iniciar sesión, pero queda en la sala de
                // espera ("Pendientes de asignar", ver Roles.razor) hasta que
                // un Administrador le asigna un rol — nunca entra con acceso
                // real solo por tener cuenta de Microsoft de la empresa.
                var nombreCompleto = infoExterna.Principal.FindFirstValue(ClaimTypes.Name) ?? email;
                usuario = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    NombreCompleto = nombreCompleto,
                    EmailConfirmed = true,
                    DebeCambiarContrasena = false,
                };

                var resultadoCreacion = await userManager.CreateAsync(usuario);
                if (!resultadoCreacion.Succeeded)
                {
                    logger.LogError(
                        "No se pudo auto-provisionar la cuenta de {Email} desde el login de Microsoft: {Errores}",
                        email, string.Join(" ", resultadoCreacion.Errors.Select(e => e.Description)));
                    await signInManager.SignOutAsync();
                    return Results.LocalRedirect("/cuenta/iniciar-sesion?errorSso=fallo");
                }
            }

            var yaVinculado = (await userManager.GetLoginsAsync(usuario))
                .Any(l => l.LoginProvider == infoExterna.LoginProvider && l.ProviderKey == infoExterna.ProviderKey);
            if (!yaVinculado)
                await userManager.AddLoginAsync(usuario, infoExterna);

            await signInManager.SignInWithClaimsAsync(usuario, isPersistent: true,
                additionalClaims: [new Claim(RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin, RestriccionLoginLocalClaimsTransformation.MetodoLoginSso)]);

            var roles = await userManager.GetRolesAsync(usuario);
            if (roles.Count == 0)
            {
                // Aviso a los Administradores solo la primera vez (al crear
                // la cuenta) — no en cada login mientras siga pendiente, para
                // no generar correo repetido. Best-effort: un fallo de envío
                // nunca debe impedir que el usuario entre a la sala de espera.
                if (esUsuarioNuevo)
                    await NotificarAdministradoresUsuarioPendienteAsync(userManager, emailService, logger, usuario);

                return Results.LocalRedirect("/cuenta/pendiente-de-rol");
            }

            return Results.LocalRedirect(string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl);
        }).AllowAnonymous();

        return endpoints;
    }

    private static async Task NotificarAdministradoresUsuarioPendienteAsync(
        UserManager<ApplicationUser> userManager, IEmailService emailService, ILogger logger, ApplicationUser usuarioPendiente)
    {
        var administradores = await userManager.GetUsersInRoleAsync(Roles.Administrador);
        var cuerpo = $"""
            <p>{System.Net.WebUtility.HtmlEncode(usuarioPendiente.NombreCompleto)} ({System.Net.WebUtility.HtmlEncode(usuarioPendiente.Email)}) inició sesión con su cuenta de Microsoft y está a la espera de que le asignes un rol.</p>
            <p>Puedes hacerlo desde la pestaña "Pendientes de asignar" en Roles.</p>
            """;

        foreach (var administrador in administradores)
        {
            if (string.IsNullOrWhiteSpace(administrador.Email)) continue;

            var resultado = await emailService.EnviarAsync(
                administrador.Email, "Nuevo usuario pendiente de asignar rol — CAE Manager", cuerpo);
            if (resultado.EsFallido)
                logger.LogWarning("No se pudo notificar a {Email} sobre un usuario pendiente de rol.", administrador.Email);
        }
    }
}
