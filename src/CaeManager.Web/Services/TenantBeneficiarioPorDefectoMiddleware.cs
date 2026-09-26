using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Components.Account;
using MediatR;
using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Web.Services;

/// <summary>
/// Decisión 5 del propietario del producto (2026-09-26, contrato del selector de
/// Tenant beneficiario): si la cartera de un Gestor CAE tiene exactamente un
/// Tenant beneficiario externo y su Tenant de origen no está gestionado, ese
/// Tenant es el activo por defecto, revalidado igual que una selección explícita.
///
/// <para>
/// <b>Dónde se fija</b>: en la ausencia de cookie de selección, no solo en el
/// primer inicio de sesión. La cookie dura 8 h y la de sesión más; si solo se
/// fijara al entrar, al caducar la selección el Gestor CAE volvería a un Tenant
/// de origen que no gestiona. Se evalúa solo en una navegación de página
/// (<see cref="NavegacionDePagina"/>), nunca
/// en <c>/_blazor</c>, en un POST ni en una llamada de fondo.
/// </para>
///
/// <para>
/// <b>Cómo, sin tocar el fallo cerrado de <see cref="RolEfectivoDelWorkspaceMiddleware"/></b>:
/// este middleware no cambia el contexto de la petición en curso. Emite la misma
/// cookie que emitiría el POST a <c>/cuenta/cliente-activo</c> —el mismo token,
/// con la operación que elige el mismo predicado
/// (<see cref="TenantsBeneficiariosAutorizados"/>)— y redirige a la misma URL. La
/// petición siguiente llega con la cookie y pasa por la sustitución de rol y la
/// revalidación exactamente como una selección explícita; nada de este
/// middleware llega a decidir un rol ni a autorizar una lectura. Si algo falla,
/// la petición sigue en el Tenant de origen, que siempre está autorizado.
/// </para>
///
/// <para>
/// Si el usuario vuelve a su Tenant de origen a propósito, el endpoint lo
/// recuerda (<see cref="CookieDeContextoTenant.NombreOrigenElegido"/>) y
/// aquí no se le vuelve a activar el Tenant por defecto.
/// </para>
/// </summary>
public class TenantBeneficiarioPorDefectoMiddleware(RequestDelegate siguiente)
{
    public async Task InvokeAsync(
        HttpContext contexto,
        IMediator mediator,
        ICurrentUserService currentUserService,
        IOperacionesQueryContext operacionesContext,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<TenantBeneficiarioPorDefectoMiddleware> logger)
    {
        if (!DebeEvaluarse(contexto))
        {
            await siguiente(contexto);
            return;
        }

        string? destino = null;
        try
        {
            destino = await FijarTenantPorDefectoAsync(
                contexto, mediator, currentUserService, operacionesContext, dataProtectionProvider);
        }
        catch (OperationCanceledException) when (contexto.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // Fallar aquí no puede conceder nada: la petición sigue en el Tenant
            // de origen. Se registra y se sirve la página.
            logger.LogWarning(ex, "No se pudo evaluar el Tenant beneficiario por defecto; la petición sigue en el Tenant de origen");
        }

        if (destino is not null)
        {
            contexto.Response.Redirect(destino);
            return;
        }

        await siguiente(contexto);
    }

    internal static bool DebeEvaluarse(HttpContext contexto)
    {
        var peticion = contexto.Request;
        return contexto.User.Identity?.IsAuthenticated == true
               && HttpMethods.IsGet(peticion.Method)
               && !CookieDeContextoTenant.HaySeleccion(peticion)
               // Las páginas de cuenta (inicio y cierre de sesión, 2FA, cambio de
               // contraseña) no dependen del Tenant activo.
               && !peticion.Path.StartsWithSegments("/cuenta", StringComparison.OrdinalIgnoreCase)
               && NavegacionDePagina.Es(peticion);
    }

    /// <returns>La URL a la que redirigir tras fijar la cookie, o <c>null</c> si no hay Tenant por defecto.</returns>
    internal static async Task<string?> FijarTenantPorDefectoAsync(
        HttpContext contexto,
        IMediator mediator,
        ICurrentUserService currentUserService,
        IOperacionesQueryContext operacionesContext,
        IDataProtectionProvider dataProtectionProvider)
    {
        var cancelacion = contexto.RequestAborted;

        if (await currentUserService.ObtenerUsuarioActualIdAsync() is not { } usuarioId
            || await currentUserService.ObtenerTenantOrigenIdAsync() is not { } tenantOrigenId
            || CookieDeContextoTenant.OrigenElegido(contexto.Request, usuarioId))
            return null;

        var autorizados = await mediator.Send(new ObtenerClientesAutorizadosQuery(), cancelacion);
        if (ClientesAutorizados.TenantPorDefecto(autorizados) is not { } porDefecto)
            return null;

        // La misma operación que embebería el POST: la selección por defecto se
        // revalida por ella en cada petición, igual que una explícita.
        if (await TenantsBeneficiariosAutorizados.OperacionQueAutorizaAsync(
                operacionesContext, usuarioId, tenantOrigenId, porDefecto.TenantId, DateTime.UtcNow, cancelacion)
            is not { } asignacionOperacionId)
            return null;

        CookieDeContextoTenant.EmitirSeleccion(
            contexto, dataProtectionProvider, usuarioId, porDefecto.TenantId, asignacionOperacionId);

        // Saneada: una ruta pedida como «//otro-sitio» no puede volverse una
        // redirección fuera de la aplicación.
        return RedireccionLocal.Sanear(
            $"{contexto.Request.PathBase}{contexto.Request.Path}{contexto.Request.QueryString}");
    }
}

public static class TenantBeneficiarioPorDefectoMiddlewareExtensions
{
    /// <summary>
    /// Después de <c>UseAuthentication</c> y de
    /// <c>UseSesionPrivilegiadaSinRolDeNegocio</c>, y ANTES de
    /// <c>UseRolEfectivoDelWorkspace</c>: la redirección tiene que salir antes de
    /// que nada resuelva el Tenant o el rol de esta petición.
    /// </summary>
    public static IApplicationBuilder UseTenantBeneficiarioPorDefecto(this IApplicationBuilder app) =>
        app.UseMiddleware<TenantBeneficiarioPorDefectoMiddleware>();
}
