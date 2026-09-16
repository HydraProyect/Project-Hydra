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
/// <b>La fuente de la rama de aprovisionamiento nunca es el token ni la
/// cookie</b> — mismo principio que <c>AutorizacionEscrituraBehavior</c> y
/// <c>AmbitoEscrituraPrivilegiada</c>. Pero, a diferencia de aquel behavior, la
/// implementación consulta <see cref="Plataforma.ISesionPrivilegiadaActual.ObtenerAsync"/>
/// (memoizado), no <c>RevalidarAsync</c>: este servicio siempre se invoca desde
/// DENTRO de un handler ya autorizado y ya elevado en ESTE MISMO comando, así
/// que la revalidación fresca del punto de mutación ya ocurrió más afuera en
/// el pipeline. Ver el comentario de la implementación para el porqué exacto
/// (hallazgo de revisión: bajo el rol de escritura acotada, una tercera
/// consulta a las tablas de plataforma habría fallado por permisos).
/// </summary>
public interface IAutorizacionEscrituraEfectiva
{
    /// <summary>
    /// <c>true</c> si el rol efectivo es <c>Administrador</c>, O si hay una
    /// sesión privilegiada (ya autorizada por el pipeline de este comando) con
    /// capacidad <c>Aprovisionamiento</c> cuyo <c>TenantObjetivoId</c> coincide
    /// con el tenant actual. Las dos ramas responden a la misma pregunta de
    /// negocio —"¿este acto tiene la autoridad de un Administrador sobre ESTE
    /// tenant?"— desde dos vías de acceso distintas.
    /// </summary>
    Task<bool> EsActoDeAdministradorAsync(CancellationToken cancellationToken = default);
}
