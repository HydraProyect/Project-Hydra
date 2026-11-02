namespace CaeManager.Application.Common;

/// <summary>
/// Sustituye la comparación literal <c>rol == "Administrador"</c> que repetían
/// <c>EjecutarImportacionCommand</c>, <c>EjecutarImportacionCombinadaCommand</c>
/// y <c>RegistrarHistorialImportacionCommand</c>: esos tres guards existían
/// para restaurar en Application el límite que ya exponían sus páginas Blazor
/// (<c>@attribute [Authorize(Roles = Administrador)]</c>), pero una sesión
/// privilegiada de Aprovisionamiento (PD-A3) no tiene rol de negocio —
/// <c>ObtenerRolEfectivoAsync</c> devuelve <c>null</c> bajo el plano 3— así que
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

    /// <summary>
    /// Autoridad del acto actual para ejecutar una carga masiva del Importador. Es una pregunta distinta de
    /// <see cref="EsActoDeAdministradorAsync"/>: el Coordinador CAE (y la Dirección CAE, por estar por encima) cargan
    /// dentro de su ámbito efectivo sin ser actos de Administrador, y el Gestor CAE no carga nunca.
    /// </summary>
    Task<AutoridadCargaMasiva> ObtenerAutoridadCargaMasivaAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Qué puede hacer el acto actual en una carga masiva. «Total» y «Acotada» operan ambas dentro del Tenant actual (no
/// son privilegio de plataforma): la diferencia es solo si cada fila se contrasta con el ámbito efectivo
/// (Asignación de Cartera) o no.
/// </summary>
public enum AutoridadCargaMasiva
{
    /// <summary>Gestor CAE, Consulta, Usuario de Cliente, o sin rol ni Sesión Privilegiada de Aprovisionamiento: no carga.</summary>
    Ninguna = 0,

    /// <summary>Acto de Administrador (rol o Sesión Privilegiada de Aprovisionamiento sobre este Tenant): sin recorte de ámbito.</summary>
    Total = 1,

    /// <summary>Coordinador CAE o Dirección CAE: cada fila se contrasta con su ámbito efectivo; lo que cae fuera se omite y se cuenta.</summary>
    Acotada = 2,
}
