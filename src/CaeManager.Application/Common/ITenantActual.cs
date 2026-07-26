namespace CaeManager.Application.Common;

/// <summary>
/// Resuelve el Tenant de la sesión/ámbito actual (ver docs/MULTITENANCY.md).
/// Es la frontera absoluta de aislamiento: <c>CaeManagerDbContext</c> lo usa
/// para el filtro global combinado con soft-delete, y el interceptor de
/// sellado lo usa para estampar <c>TenantId</c> en cada entidad nueva.
///
/// Deliberadamente síncrono (no <c>Task&lt;Guid?&gt;</c> como
/// <see cref="ICurrentUserService"/>): un EF Core <c>HasQueryFilter</c> debe
/// poder evaluar esta propiedad de forma síncrona en cada consulta. La
/// implementación real (Web) resuelve el valor una sola vez por ámbito a
/// partir del claim <c>tenant_id</c> de la sesión — ver
/// docs/MULTITENANCY.md § 8 (Tenant Resolution Strategy).
///
/// <c>null</c> significa "sin tenant resuelto" — el filtro global interpreta
/// esto como "no ver ninguna fila" (fallo cerrado), nunca como "sin
/// restricción".
/// </summary>
public interface ITenantActual
{
    Guid? TenantId { get; }
}
