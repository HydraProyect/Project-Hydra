using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Web.Services;

/// <summary>
/// El <see cref="IServiceProvider"/> no es pereza de diseño: es lo que rompe
/// un ciclo de DI real. <c>CaeManagerDbContext</c> monta
/// <c>AuditoriaInterceptor</c>, que depende de este mismo servicio, así que
/// inyectar el contexto por constructor deja el grafo sin resolver y la
/// aplicación no arranca. Resolverlo dentro del método funciona porque el
/// único punto que necesita base de datos —el rol de una delegación— nunca
/// se llama desde el interceptor, que solo pide el Id de usuario y no toca
/// la base de datos.
/// </summary>
public class CurrentUserService(
    AuthenticationStateProvider authenticationStateProvider,
    IHttpContextAccessor httpContextAccessor,
    IClienteActivoSeleccionado clienteActivoSeleccionado,
    IServiceProvider serviceProvider) : ICurrentUserService
{
    public async Task<Guid?> ObtenerUsuarioActualIdAsync()
    {
        var usuario = await ObtenerUsuarioAsync();
        if (usuario is null) return null;

        var valorClaim = usuario.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return Guid.TryParse(valorClaim, out var usuarioId) ? usuarioId : null;
    }

    /// <summary>
    /// El rol <b>efectivo en el contexto actual</b>, no el del claim de
    /// sesión. Mientras se opera un Delegated Workspace manda el rol de la
    /// <c>AsignacionOperadorDelegado</c> de esa delegación, que es lo que
    /// ADR-004 § 5.3 promete al decir que un mismo usuario puede ser GestorCae
    /// en un cliente y Consulta en otro.
    ///
    /// Antes se devolvía siempre el claim, así que el rol de la asignación se
    /// guardaba y no se leía jamás: un operador asignado como Consulta sobre
    /// el tenant B, pero Administrador en el suyo, escribía en B con
    /// privilegios que nadie le había dado ahí (hallazgo N-5 de
    /// INFORME-AUDITORIA-2.md).
    ///
    /// Fallo cerrado: si se está operando un workspace delegado y no aparece
    /// asignación viva —porque la delegación se revocó mientras el token de
    /// selección seguía vigente— devuelve null, y ningún rol es peor que
    /// cualquier rol. Por eso <c>AutorizacionEscrituraBehavior</c> decide por
    /// lista blanca: con lista negra, "sin rol" habría dejado escribir.
    /// </summary>
    public async Task<string?> ObtenerRolActualAsync()
    {
        var usuario = await ObtenerUsuarioAsync();
        var rolDeSesion = usuario?.FindFirst(ClaimTypes.Role)?.Value;

        // Sin selección no hay delegación en juego: el caso de todo usuario
        // que no es Operador Delegado de nadie, sin ninguna consulta extra.
        if (clienteActivoSeleccionado.TenantIdSeleccionado is not { } tenantSeleccionado)
            return rolDeSesion;

        var usuarioId = await ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return null;

        // Se comprueba contra la delegación viva, no contra lo que dijera el
        // token al emitirse — una revocación tiene que notarse aquí.
        var dbContext = serviceProvider.GetRequiredService<IApplicationDbContext>();

        return await (
            from asignacion in dbContext.AsignacionesOperadorDelegado
            join delegacion in dbContext.DelegacionesTenant on asignacion.DelegacionTenantId equals delegacion.Id
            where asignacion.UsuarioId == usuarioId.Value
                  && delegacion.Activa
                  && delegacion.TenantClienteId == tenantSeleccionado
            select asignacion.Rol)
            .FirstOrDefaultAsync();
    }

    public async Task<Guid?> ObtenerTenantOrigenIdAsync()
    {
        var usuario = await ObtenerUsuarioAsync();
        var valorClaim = usuario?.FindFirst(TenantClaimsPrincipalFactory.TipoClaimTenantId)?.Value;
        return Guid.TryParse(valorClaim, out var tenantId) ? tenantId : null;
    }

    // Dentro de un circuito de Blazor, AuthenticationStateProvider ya trae el
    // ClaimsPrincipal correcto (capturado al negociar el circuito). Fuera de
    // uno — endpoints minimal API como GET /documentos/{id}/archivo, que no
    // tienen circuito pero sí HttpContext.User ya autenticado por la cookie
    // de Identity — hace falta el fallback a IHttpContextAccessor; si tampoco
    // hay HttpContext (migraciones/siembra al arrancar, jobs en segundo
    // plano), no hay usuario que auditar.
    private async Task<ClaimsPrincipal?> ObtenerUsuarioAsync()
    {
        try
        {
            var estado = await authenticationStateProvider.GetAuthenticationStateAsync();
            if (estado.User.Identity?.IsAuthenticated == true)
                return estado.User;
        }
        catch (InvalidOperationException)
        {
            // sin circuito de Blazor — se intenta el fallback de abajo.
        }

        var usuarioHttp = httpContextAccessor.HttpContext?.User;
        return usuarioHttp?.Identity?.IsAuthenticated == true ? usuarioHttp : null;
    }
}
