namespace CaeManager.Application.Common;

/// <summary>
/// Abstrae la identidad del usuario autenticado frente a Infrastructure
/// (auditoría) y los handlers de Application. La implementación real vive en
/// Web, que es la única capa con acceso al contexto de autenticación de
/// Blazor Server.
/// </summary>
public interface ICurrentUserService
{
    Task<Guid?> ObtenerUsuarioActualIdAsync();

    /// <summary>
    /// Rol <b>efectivo</b> del usuario en el Tenant que se está operando ahora
    /// (el sistema asigna exactamente uno por usuario y contexto). Es el único
    /// rol que vale para autorización, alcance de datos, middleware y la UI
    /// del Tenant actual:
    /// <list type="bullet">
    /// <item>sin Workspace operativo derivado, el rol de la sesión en el
    /// Tenant de origen (con las restricciones de sesión ya aplicadas, p. ej.
    /// login local);</item>
    /// <item>en un Workspace operativo derivado, el de la Asignación de
    /// Cartera (o de la delegación heredada) en el Tenant propietario;</item>
    /// <item>bajo <c>AmbitoTenantExplicito</c> (fan-out multi-Tenant), el del
    /// Tenant visitado en cada vuelta — el de sesión si es el de origen;</item>
    /// <item>null bajo una Sesión Privilegiada de plataforma, sin sesión, o
    /// sin asignación viva (fallo cerrado).</item>
    /// </list>
    /// Decisión P7 (2026-09-23): nunca usar el rol de origen en su lugar.
    /// </summary>
    Task<string?> ObtenerRolEfectivoAsync();

    /// <summary>
    /// Rol del usuario <b>en su organización de origen</b>, leído en Identity
    /// (<c>AspNetUserRoles</c>) y no del claim de sesión, que dentro de un
    /// Workspace operativo derivado ya está sustituido por el rol efectivo.
    /// Sirve para identidad, informes y pertenencia a la organización (p. ej.
    /// a la del Operador CAE). <b>Nunca</b> para autorización ni alcance: no
    /// refleja la cartera del Tenant operado ni las restricciones de sesión
    /// (login local), y un trinquete de arquitectura lo prohíbe allí
    /// (<c>RolDeOrigenFueraDeAutorizacionTests</c>). Null sin usuario o sin
    /// rol asignado. No depende del Tenant operado ni de una Sesión
    /// Privilegiada: es un dato de identidad de la cuenta.
    /// </summary>
    Task<string?> ObtenerRolOrigenAsync();

    /// <summary>
    /// Alias obsoleto de <see cref="ObtenerRolEfectivoAsync"/> (decisión P7,
    /// 2026-09-23). El nombre no decía qué rol devolvía y dejó que algún
    /// consumidor lo leyera como el rol de origen.
    /// </summary>
    [Obsolete("Usa ObtenerRolEfectivoAsync (autorización, alcance, UI del Tenant actual) u ObtenerRolOrigenAsync (identidad, informes, pertenencia). Decisión P7.")]
    Task<string?> ObtenerRolActualAsync() => ObtenerRolEfectivoAsync();

    /// <summary>
    /// Tenant de origen del usuario (el claim de sesión, ver
    /// docs/MULTITENANCY.md § 8) — a diferencia de <c>ITenantActual.TenantId</c>,
    /// nunca refleja un Delegated Workspace elegido (ADR-004 § 6); es
    /// siempre el tenant al que pertenece la cuenta. Null en las mismas
    /// condiciones que <see cref="ObtenerUsuarioActualIdAsync"/>.
    /// </summary>
    Task<Guid?> ObtenerTenantOrigenIdAsync();

    /// <summary>
    /// Si el usuario actual tiene la autenticación en dos pasos activada
    /// (P1-13 de docs/business/MATURITY_REVIEW.md — se exige para abrir un
    /// acceso de soporte, ver AbrirAccesoSoporteCommand). Application no
    /// puede consultar Identity directamente (vive en Infrastructure/Web);
    /// falso también si no hay usuario resuelto — fallo cerrado, igual que
    /// <see cref="ObtenerRolEfectivoAsync"/>.
    /// </summary>
    Task<bool> TieneDobleFactorActivoAsync();
}
