namespace CaeManager.Application.Common;

/// <summary>
/// Delegated Workspace activo elegido explícitamente por el usuario —
/// cuarto modo de la Tenant Resolution Strategy (ver docs/MULTITENANCY.md
/// § 8 y ADR-004-delegacion-consultoras-cae.md § 6).
///
/// <see cref="TenantIdSeleccionado"/> es null cuando el usuario no ha
/// elegido ningún cliente distinto del suyo — <c>ITenantActual</c> cae
/// entonces al claim de sesión de siempre, comportamiento idéntico al actual
/// para cualquier usuario que no sea Operador Delegado de nadie (el caso de
/// hoy, cero cambio de UX).
///
/// La elección se revalida contra <c>DelegacionTenant</c> +
/// <c>AsignacionOperadorDelegado</c> del usuario actual en el momento de
/// elegir (ver el endpoint <c>/cuenta/cliente-activo/{tenantId}</c>, Web) —
/// nunca se confía en un valor sin comprobar primero que la delegación
/// sigue activa. El valor validado se persiste en una cookie y esta
/// interfaz solo lo lee — no expone una forma de "elegir" directamente
/// porque la elección implica una revalidación en base de datos y un
/// redirect completo (ver el comentario de por qué en
/// <c>ClienteActivoSeleccionado</c>, Web).
/// </summary>
public interface IClienteActivoSeleccionado
{
    Guid? TenantIdSeleccionado { get; }
}
