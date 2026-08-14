using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Account;
using CaeManager.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace CaeManager.Web.Features.Tenants;

/// <summary>
/// Cambia la vista previa de vocabulario (DDL-072) del Administrador —
/// endpoint HTTP y no un Command de MediatR por el mismo motivo que
/// <see cref="ClienteActivoEndpoints"/>: escribir una cookie de respuesta y
/// redirigir solo tiene sentido en el ciclo de vida de una petición HTTP
/// normal, no dentro de un circuito de Blazor Server ya establecido.
/// </summary>
public static class VistaVocabularioPreviewEndpoints
{
    public static IEndpointRouteBuilder MapVistaVocabularioPreviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // POST, no GET: cambia estado del lado del servidor. UseAntiforgery
        // valida el token del formulario automáticamente (ver Program.cs).
        endpoints.MapPost("/cuenta/vista-vocabulario", (
            HttpContext httpContext, [FromForm] string? perfil, [FromForm] string? returnUrl) =>
        {
            if (!httpContext.User.IsInRole(Roles.Administrador))
                return Results.Forbid();

            if (Enum.TryParse<PerfilVocabularioTenant>(perfil, out var perfilForzado))
            {
                httpContext.Response.Cookies.Append(VistaVocabularioPreviewCookie.NombreCookie, perfilForzado.ToString(), new CookieOptions
                {
                    HttpOnly = true,
                    Secure = httpContext.Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    MaxAge = VistaVocabularioPreviewCookie.Vigencia,
                });
            }
            else
            {
                // Cualquier otro valor (incluido "" para "Automático") vuelve al perfil real del tenant.
                httpContext.Response.Cookies.Delete(VistaVocabularioPreviewCookie.NombreCookie);
            }

            return Results.LocalRedirect(RedireccionLocal.Sanear(returnUrl));
        });

        return endpoints;
    }
}
