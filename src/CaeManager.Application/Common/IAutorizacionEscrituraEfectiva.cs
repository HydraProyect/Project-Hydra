namespace CaeManager.Application.Common;

/// <summary>
/// Sustituye la comparación literal <c>rol == "Administrador"</c> que repetían
/// <c>EjecutarImportacionCommand</c>, <c>EjecutarImportacionCombinadaCommand</c>
/// y <c>RegistrarHistorialImportacionCommand</c>: esos tres guards existían
/// para restaurar en Application el límite que ya exponían sus páginas Blazor
/// (<c>@attribute [Authorize(Roles = Administrador)]</c>), pero una sesión
/// privilegiada de Aprovisionamiento (PD-A3) no tiene rol de negocio —
/// <c>ObtenerRolActualAsync</c> devuelve <c>null</c> bajo el plano 3— así que
/// la comparación literal bloqueaba también al camino que el resto de PD-A3
/// acaba de abrir.
///
/// <b>La fuente de la rama de aprovisionamiento es siempre la sesión
/// REVALIDADA</b> (<see cref="Plataforma.ISesionPrivilegiadaActual.RevalidarAsync"/>),
/// nunca el token ni la cookie — mismo principio que
/// <c>AutorizacionEscrituraBehavior</c> y <c>AmbitoEscrituraPrivilegiada</c>.
/// </summary>
public interface IAutorizacionEscrituraEfectiva
{
    /// <summary>
    /// <c>true</c> si el rol efectivo es <c>Administrador</c>, O si hay una
    /// sesión privilegiada revalidada con capacidad <c>Aprovisionamiento</c>
    /// cuyo <c>TenantObjetivoId</c> coincide con el tenant actual. Las dos
    /// ramas responden a la misma pregunta de negocio —"¿este acto tiene la
    /// autoridad de un Administrador sobre ESTE tenant?"— desde dos vías de
    /// acceso distintas.
    /// </summary>
    Task<bool> EsActoDeAdministradorAsync(CancellationToken cancellationToken = default);
}
