using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Web.Components.Account;
using CaeManager.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

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
        endpoints.MapPost("/cuenta/cliente-activo", (
            [FromForm] Guid tenantId, [FromForm] string? returnUrl, HttpContext httpContext,
            ITenantsQueryContext dbContext, ICurrentUserService currentUserService,
            IOperacionesQueryContext operacionesContext,
            IDataProtectionProvider dataProtectionProvider,
            IContadorPendientesSelectorTenant contadorPendientes,
            CancellationToken cancellationToken) =>
            CambiarAsync(
                tenantId, returnUrl, httpContext, dbContext, currentUserService,
                operacionesContext, dataProtectionProvider, cancellationToken, contadorPendientes));

        return endpoints;
    }

    /// <summary>
    /// El cuerpo del POST. Un lambda dentro de <c>MapPost</c> no se puede llamar;
    /// un método sí, y así el test de integración ejerce el endpoint real contra
    /// PostgreSQL (mismo patrón que <c>SesionSoporteEndpoints.AbrirAsync</c>).
    /// </summary>
    public static async Task<IResult> CambiarAsync(
        Guid tenantId, string? returnUrl, HttpContext httpContext,
        ITenantsQueryContext dbContext, ICurrentUserService currentUserService,
        IOperacionesQueryContext operacionesContext,
        IDataProtectionProvider dataProtectionProvider,
        CancellationToken cancellationToken,
        IContadorPendientesSelectorTenant? contadorPendientes = null)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();

        if (usuarioId is null || tenantOrigenId is null)
            return Results.Unauthorized();

        var ahora = DateTime.UtcNow;

        // Las tres vías del predicado único (TenantsBeneficiariosAutorizados),
        // el mismo que aplican la lista del selector y la revalidación
        // (invariante I2): nunca una copia aquí.
        //
        // La operación por la que este usuario puede abrir ese Tenant. Se
        // resuelve exigiendo cartera vigente, así que encontrarla ya es
        // autorización suficiente por sí sola, y se embebe en el token para
        // revalidar esa misma operación en cada petición. Determinista
        // (REC-136): la vigente más reciente y, a igualdad, la de menor Id.
        var asignacionOperacionId = tenantId == tenantOrigenId.Value
            ? null
            : await TenantsBeneficiariosAutorizados.OperacionQueAutorizaAsync(
                operacionesContext, usuarioId.Value, tenantOrigenId.Value, tenantId, ahora, cancellationToken);

        // El Tenant de origen del usuario siempre está autorizado sobre sí
        // mismo. La vía heredada se conserva porque es la del acceso de
        // soporte, que todavía no tiene operación propia.
        var autorizado = tenantId == tenantOrigenId.Value || asignacionOperacionId is not null
            || await TenantsBeneficiariosAutorizados.AutorizadoPorViaHeredadaAsync(
                dbContext, usuarioId.Value, tenantId, ahora, cancellationToken);

        if (!autorizado)
            return Results.Forbid();

        if (tenantId == tenantOrigenId.Value)
            CookieDeContextoTenant.VolverAlOrigen(httpContext, usuarioId.Value);
        else
        {
            CookieDeContextoTenant.EmitirSeleccion(
                httpContext, dataProtectionProvider, usuarioId.Value, tenantId, asignacionOperacionId);
            // Preferencia de interfaz («Recientes» del selector), solo tras la autorización de arriba.
            RecientesSelectorTenant.Registrar(httpContext, usuarioId.Value, tenantId);
        }

        // Cambiar de empresa descarta los pendientes calculados para este usuario.
        contadorPendientes?.Invalidar(usuarioId.Value);

        // Saneado explícito (no solo LocalRedirect) por la misma razón que
        // IdentityEndpointsExtensions: un returnUrl malicioso hace que
        // LocalRedirect lance en vez de responder — saneado, la petición
        // simplemente aterriza en "/" (hallazgo de la auditoría de PR #48
        // sobre P0-2, mismo patrón que Login.razor/LoginCon2fa.razor).
        return Results.LocalRedirect(RedireccionLocal.Sanear(returnUrl));
    }
}
