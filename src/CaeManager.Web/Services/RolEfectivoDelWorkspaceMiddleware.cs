using System.Security.Claims;
using CaeManager.Application.Common;

namespace CaeManager.Web.Services;

/// <summary>
/// Sustituye los claims de rol por el rol <b>efectivo en el workspace
/// delegado activo</b> mientras se opera uno.
///
/// <para>
/// <b>El agujero que cierra.</b> ADR-004 § 5.3 promete que un mismo usuario
/// puede ser GestorCae en un cliente y Consulta en otro, y
/// <c>CurrentUserService.ObtenerRolActualAsync</c> lo cumple: resuelve el rol
/// contra la cartera de la operación seleccionada. Pero hay una segunda familia
/// de puertas que no consulta ese método jamás — los <c>[Authorize(Roles = …)]</c>
/// de páginas y endpoints, que preguntan directamente al
/// <c>ClaimsPrincipal</c>—, y ahí seguía contestando el rol del <b>tenant de
/// origen</b>. Un Administrador del tenant A, delegado como Consulta en el
/// tenant B, superaba con el claim de A las puertas de Administrador de B:
/// Configuración, Roles, Claves de API, Auditoría, Integraciones e
/// Importaciones del cliente que solo debía poder mirar. Escalada horizontal y
/// vertical a la vez, y precisamente el hallazgo N-5 que
/// <c>ObtenerRolActualAsync</c> creía haber cerrado — lo cerró en el camino de
/// escritura, no en el de las puertas de página.
/// </para>
///
/// <para>
/// <b>Por qué sustituir y no quitar.</b> El plano 3 (sesión privilegiada de
/// plataforma) se resuelve quitando el rol entero, porque quien entra por
/// privilegio no es miembro del workspace y no debe tener ninguno
/// (<see cref="SesionPrivilegiadaSinRolDeNegocioMiddleware"/>). El plano 2 es
/// distinto: el operador delegado <b>sí</b> es miembro, con el rol que su
/// cartera le da. Quitarlo dejaría la delegación sin acceso a nada —el motivo
/// por el que el middleware del plano 3 conserva a propósito el claim aquí— y
/// conservarlo intacto es el agujero. La respuesta correcta no era ninguna de
/// las dos: es poner el rol que corresponde a este workspace.
/// </para>
///
/// <para>
/// <b>Por qué reutiliza <c>ObtenerRolActualAsync</c> en vez de repetir la
/// consulta.</b> Porque dos resoluciones del mismo concepto divergen, y la
/// divergencia sería invisible: las puertas de página dirían una cosa y
/// <c>AutorizacionEscrituraBehavior</c> otra. Con una sola fuente, un cambio en
/// las reglas de cartera llega a las dos a la vez. Hereda además su fallo
/// cerrado: si la delegación se revocó mientras el token seguía vigente,
/// devuelve <c>null</c>, se retiran todos los claims de rol y las puertas
/// fallan cerradas — sin esperar a que
/// <see cref="RevalidacionClienteActivoMiddleware"/> invalide la selección más
/// adelante en el pipeline.
/// </para>
///
/// <para>
/// <b>Posición y coste.</b> Entre <c>UseAuthentication</c> y
/// <c>UseAuthorization</c>, después del middleware del plano 3: es la ventana
/// en la que el principal ya existe y todavía no lo ha leído ninguna puerta.
/// Solo paga quien trae cookie de selección —los Operadores Delegados—, que ya
/// pagan una consulta equivalente en <see cref="RevalidacionClienteActivoMiddleware"/>.
/// Para todos los demás el middleware mira una cabecera y sigue.
/// </para>
///
/// <para>
/// El principal modificado alcanza al circuito de Blazor porque cambiar de
/// workspace exige una recarga completa del navegador (ver
/// <see cref="ClienteActivoSeleccionado"/> y <c>ClienteActivoEndpoints</c>), así
/// que el circuito nuevo se negocia sobre esta misma petición HTTP.
/// </para>
/// </summary>
public class RolEfectivoDelWorkspaceMiddleware(RequestDelegate siguiente)
{
    public async Task InvokeAsync(
        HttpContext contexto,
        IClienteActivoSeleccionado clienteActivoSeleccionado,
        ICurrentUserService currentUserService)
    {
        if (DebeAjustarse(contexto, clienteActivoSeleccionado))
            AplicarRol(contexto.User, await currentUserService.ObtenerRolActualAsync());

        await siguiente(contexto);
    }

    private static bool DebeAjustarse(HttpContext contexto, IClienteActivoSeleccionado seleccion)
    {
        // Sin cookie no hay selección que valga, y no se descifra nada: el caso
        // de la inmensa mayoría de las peticiones.
        if (string.IsNullOrEmpty(contexto.Request.Cookies[ClienteActivoSeleccionado.NombreCookie]))
            return false;

        // Un usuario sin autenticar no tiene rol que ajustar, y consultar la
        // base por él sería trabajo regalado a quien todavía no ha entrado.
        if (contexto.User.Identity?.IsAuthenticated != true)
            return false;

        // El plano 3 ya lo resolvió el middleware anterior quitando el rol
        // entero. Volver a tocarlo aquí solo podría devolvérselo.
        if (seleccion.SesionPrivilegiadaIdSeleccionada is not null)
            return false;

        // Un token manipulado, caducado o de otro usuario resuelve a null en la
        // abstracción, y entonces no hay workspace delegado: manda el claim de
        // sesión, que es el del tenant propio del usuario.
        return seleccion.TenantIdSeleccionado is not null;
    }

    /// <summary>
    /// Deja el principal con exactamente un rol —el efectivo— o con ninguno.
    /// Se recorren todas las identidades, no solo la primera: un principal
    /// puede llevar varias, y dejar una con el rol viejo bastaría para que
    /// <c>IsInRole</c> siguiera contestando que sí.
    /// </summary>
    private static void AplicarRol(ClaimsPrincipal principal, string? rolEfectivo)
    {
        foreach (var identidad in principal.Identities)
            foreach (var claimRol in identidad.FindAll(identidad.RoleClaimType).ToList())
                identidad.RemoveClaim(claimRol);

        if (rolEfectivo is null) return;

        // Sobre la identidad principal, y con SU tipo de claim de rol: añadirlo
        // con ClaimTypes.Role a secas funcionaría por casualidad solo mientras
        // el esquema no lo cambie, y IsInRole compara contra RoleClaimType.
        if (principal.Identity is ClaimsIdentity identidadPrincipal)
            identidadPrincipal.AddClaim(new Claim(identidadPrincipal.RoleClaimType, rolEfectivo));
    }
}

public static class RolEfectivoDelWorkspaceMiddlewareExtensions
{
    /// <summary>
    /// Registrar entre <c>UseAuthentication</c> y <c>UseAuthorization</c>, y
    /// después de <c>UseSesionPrivilegiadaSinRolDeNegocio</c>. El orden no es
    /// preferencia: después de la segunda, las puertas de rol ya habrían
    /// contestado con el rol del tenant equivocado.
    /// </summary>
    public static IApplicationBuilder UseRolEfectivoDelWorkspace(this IApplicationBuilder app) =>
        app.UseMiddleware<RolEfectivoDelWorkspaceMiddleware>();
}
