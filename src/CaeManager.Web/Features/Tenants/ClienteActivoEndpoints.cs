using CaeManager.Application.Common;
using CaeManager.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Web.Features.Tenants;

/// <summary>
/// Cambia el Delegated Workspace activo (ADR-004 § 6) — endpoint HTTP en vez
/// de un Command de MediatR porque su efecto (escribir una cookie de
/// respuesta y redirigir) solo tiene sentido en el ciclo de vida de una
/// petición HTTP normal, no dentro de un circuito de Blazor Server ya
/// establecido (ver <see cref="ClienteActivoSeleccionado"/> para el motivo
/// completo: cambiar de cliente exige un reload de navegador, y ese reload
/// necesita algo que sobreviva al circuito viejo antes de que exista el
/// nuevo).
/// </summary>
public static class ClienteActivoEndpoints
{
    public static IEndpointRouteBuilder MapClienteActivoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/cuenta/cliente-activo/{tenantId:guid}", async (
            Guid tenantId, string returnUrl, HttpContext httpContext,
            IApplicationDbContext dbContext, ICurrentUserService currentUserService,
            CancellationToken cancellationToken) =>
        {
            var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
            var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();

            if (usuarioId is null || tenantOrigenId is null)
                return Results.Unauthorized();

            // El tenant de origen del usuario siempre está autorizado sobre
            // sí mismo — mismo criterio que ObtenerClientesAutorizadosQuery.
            var autorizado = tenantId == tenantOrigenId.Value || await (
                from asignacion in dbContext.AsignacionesOperadorDelegado
                join delegacion in dbContext.DelegacionesTenant on asignacion.DelegacionTenantId equals delegacion.Id
                where asignacion.UsuarioId == usuarioId.Value && delegacion.Activa && delegacion.TenantClienteId == tenantId
                select delegacion.Id)
                .AnyAsync(cancellationToken);

            if (!autorizado)
                return Results.Forbid();

            if (tenantId == tenantOrigenId.Value)
            {
                // Volver al propio tenant de origen: basta con borrar la
                // cookie, no hace falta que "seleccione explícitamente" su
                // propio tenant — así el claim de sesión vuelve a mandar.
                httpContext.Response.Cookies.Delete(ClienteActivoSeleccionado.NombreCookie);
            }
            else
            {
                httpContext.Response.Cookies.Append(ClienteActivoSeleccionado.NombreCookie, tenantId.ToString(), new CookieOptions
                {
                    HttpOnly = true,
                    // Igual que la política por defecto de la cookie de Identity
                    // (SameAsRequest): en local sobre HTTP la marca Secure haría
                    // que el navegador descartara la cookie sin avisar. Detrás
                    // del proxy de despliegue, UseForwardedHeaders ya hace que
                    // IsHttps refleje el esquema original, no el interno.
                    Secure = httpContext.Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    MaxAge = TimeSpan.FromHours(12),
                });
            }

            // LocalRedirect (no Redirect) a propósito: rechaza cualquier
            // returnUrl que no sea una ruta local — nunca seguir una URL
            // externa que llegara en el query string.
            return Results.LocalRedirect(string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl);
        });

        return endpoints;
    }
}
