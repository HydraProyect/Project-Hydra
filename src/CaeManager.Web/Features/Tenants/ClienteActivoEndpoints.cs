using CaeManager.Application.Common;
using CaeManager.Web.Components.Account;
using CaeManager.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
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
        // POST, no GET: cambia estado del lado del servidor (escribe/borra la
        // cookie de workspace activo), así que no puede viajar en una URL que
        // alguien pueda hacer seguir a un usuario con un enlace. Al aceptar
        // formulario, UseAntiforgery valida el token automáticamente y una
        // navegación de nivel superior provocada desde fuera ya no basta.
        endpoints.MapPost("/cuenta/cliente-activo", async (
            [FromForm] Guid tenantId, [FromForm] string? returnUrl, HttpContext httpContext,
            IApplicationDbContext dbContext, ICurrentUserService currentUserService,
            IDataProtectionProvider dataProtectionProvider,
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
                // Activa y no caducada — ver DelegacionTenant.EstaVigente.
                where asignacion.UsuarioId == usuarioId.Value && delegacion.Activa
                      && delegacion.TenantClienteId == tenantId
                      && (delegacion.ExpiraEnUtc == null || delegacion.ExpiraEnUtc > DateTime.UtcNow)
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
                // Token protegido y ligado a este usuario, no el GUID en
                // claro: esta autorización se comprueba al escribir, y el
                // sellado criptográfico es lo que hace que siga valiendo al
                // leer. Sin él, la comprobación de arriba era trivialmente
                // esquivable escribiendo la cookie a mano (C-1).
                var token = ClienteActivoSeleccionado.Proteger(dataProtectionProvider, usuarioId.Value, tenantId);

                httpContext.Response.Cookies.Append(ClienteActivoSeleccionado.NombreCookie, token, new CookieOptions
                {
                    HttpOnly = true,
                    // Igual que la política por defecto de la cookie de Identity
                    // (SameAsRequest): en local sobre HTTP la marca Secure haría
                    // que el navegador descartara la cookie sin avisar. Detrás
                    // del proxy de despliegue, UseForwardedHeaders ya hace que
                    // IsHttps refleje el esquema original, no el interno.
                    Secure = httpContext.Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    // Misma vigencia que la del propio token, que es la que
                    // de verdad se comprueba en servidor al descifrarlo.
                    MaxAge = ClienteActivoSeleccionado.Vigencia,
                });
            }

            // Saneado explícito (no solo LocalRedirect) por la misma razón que
            // IdentityEndpointsExtensions: un returnUrl malicioso hace que
            // LocalRedirect lance en vez de responder — saneado, la petición
            // simplemente aterriza en "/" (hallazgo de la auditoría de PR #48
            // sobre P0-2, mismo patrón que Login.razor/LoginCon2fa.razor).
            return Results.LocalRedirect(RedireccionLocal.Sanear(returnUrl));
        });

        return endpoints;
    }
}
