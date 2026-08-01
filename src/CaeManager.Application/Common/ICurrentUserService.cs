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
    /// Rol del usuario actual (el sistema asigna exactamente uno por
    /// usuario, ver Usuarios.razor). Null fuera de un circuito de Blazor
    /// (igual que ObtenerUsuarioActualIdAsync) o si el usuario no tiene
    /// ningún rol asignado.
    /// </summary>
    Task<string?> ObtenerRolActualAsync();

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
    /// <see cref="ObtenerRolActualAsync"/>.
    /// </summary>
    Task<bool> TieneDobleFactorActivoAsync();
}
