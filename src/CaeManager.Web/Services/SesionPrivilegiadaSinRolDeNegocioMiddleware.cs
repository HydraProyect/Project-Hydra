using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Web.Services;

/// <summary>
/// Quita del principal todos los claims de rol mientras el token de selección
/// nombre una sesión privilegiada de plataforma.
///
/// <b>Por qué hace falta un middleware y no basta con
/// <c>CurrentUserService.ObtenerRolActualAsync</c>.</b> Ese método gobierna la
/// autorización que pasa por la aplicación (alcance de datos, behavior de
/// escritura) y ya devuelve <c>null</c> bajo sesión privilegiada. Pero hay una
/// segunda familia de puertas que no lo consulta jamás: los 28
/// <c>[Authorize(Roles = …)]</c> de páginas y endpoints, que preguntan
/// directamente al <c>ClaimsPrincipal</c>. Ahí el rol que contesta es el que el
/// técnico tiene en <b>su</b> tenant de plataforma — una autoridad que no tiene
/// nada que ver con el tenant que está visitando. Un técnico que sea
/// Administrador en TALVEG entraría por esa vía en Configuración, Roles, Claves
/// de API o Auditoría del cliente sin que nadie se lo hubiera concedido.
///
/// Es exactamente el patrón que <c>AutorizacionEscrituraBehavior</c> evita a
/// propósito: que la autorización dependa de un valor que resulta ser el
/// correcto por casualidad. Aquí ni siquiera lo es — es el incorrecto.
///
/// La regla del ADR-011 § 4bis.3 es que las tres capas se evalúan por separado
/// y que la autorización <i>de negocio</i> no aplica a una sesión de plano 3:
/// quien entra por privilegio de plataforma no es miembro del workspace que
/// visita. Sin rol, esas 28 puertas fallan cerradas, que es la única forma
/// segura de equivocarse. Lo que la sesión sí concede —lectura del tenant
/// objetivo— se concede por capacidad en <c>AlcanceDatosService</c>, no por rol.
///
/// <b>Posición en el pipeline.</b> Justo después de <c>UseAuthentication</c> y
/// antes de <c>UseAuthorization</c>: es la ventana en la que el principal ya
/// existe y todavía no lo ha leído ninguna puerta. Por eso no puede vivir dentro
/// de <see cref="RevalidacionClienteActivoMiddleware"/>, que corre después de
/// <c>UseAuthorization</c> a propósito.
///
/// <b>Por qué decide con el token y no revalidando contra la base.</b> Porque
/// solo quita permisos. Un token que mienta al nombrar una sesión no gana nada:
/// pierde su rol. Y si la sesión no vale, la revalidación posterior invalida la
/// selección entera y el contexto cae al tenant propio del usuario — sin rol
/// durante esa petición, que es un inconveniente y no un agujero. Revalidar aquí
/// costaría una consulta a base de datos en el camino de <b>todas</b> las
/// peticiones autenticadas del sistema para no cambiar ninguna decisión.
///
/// Coste para el resto del mundo: leer una cabecera de cookie. Solo quien trae
/// cookie de selección paga además un descifrado.
/// </summary>
public class SesionPrivilegiadaSinRolDeNegocioMiddleware(RequestDelegate siguiente)
{
    public async Task InvokeAsync(HttpContext contexto, IDataProtectionProvider dataProtectionProvider)
    {
        if (TraeSesionPrivilegiada(contexto, dataProtectionProvider))
            QuitarClaimsDeRol(contexto.User);

        await siguiente(contexto);
    }

    private static bool TraeSesionPrivilegiada(HttpContext contexto, IDataProtectionProvider dataProtectionProvider)
    {
        // Sin cookie no hay nada que mirar: el caso de la inmensa mayoría, y
        // sin descifrar nada.
        var valorCookie = contexto.Request.Cookies[ClienteActivoSeleccionado.NombreCookie];
        if (string.IsNullOrEmpty(valorCookie)) return false;

        var leido = ClienteActivoSeleccionado.LeerCargaUtil(
            dataProtectionProvider, valorCookie, ClienteActivoSeleccionado.LeerUsuarioActual(contexto.User));

        return leido.SesionPrivilegiadaId is not null;
    }

    /// <summary>
    /// Todos los claims de rol de todas las identidades, no solo de la
    /// primera: un principal puede llevar varias, y dejar una con rol
    /// bastaría para que <c>IsInRole</c> siguiera diciendo que sí.
    /// </summary>
    private static void QuitarClaimsDeRol(ClaimsPrincipal principal)
    {
        foreach (var identidad in principal.Identities)
            foreach (var claimRol in identidad.FindAll(identidad.RoleClaimType).ToList())
                identidad.RemoveClaim(claimRol);
    }
}

public static class SesionPrivilegiadaSinRolDeNegocioMiddlewareExtensions
{
    /// <summary>
    /// Registrar entre <c>UseAuthentication</c> y <c>UseAuthorization</c>. El
    /// orden no es preferencia: después de la segunda, las puertas de rol ya
    /// habrían contestado.
    /// </summary>
    public static IApplicationBuilder UseSesionPrivilegiadaSinRolDeNegocio(this IApplicationBuilder app) =>
        app.UseMiddleware<SesionPrivilegiadaSinRolDeNegocioMiddleware>();
}
