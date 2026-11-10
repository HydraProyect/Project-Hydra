using CaeManager.Domain.Common;

namespace CaeManager.Application.Plataforma;

/// <summary>
/// La escritura de <c>CambiarSegundoFactorDelTenantPorPlataformaCommand</c>: la conexión de una
/// Sesión Privilegiada es de solo lectura, y <c>app_cambiar_segundo_factor_tenant_por_plataforma</c>
/// es su única puerta para este acto. La función repite cada precondición, escribe solo la exigencia
/// de segundo factor del Tenant objetivo y audita en la misma transacción. No recibe el plazo de
/// inactividad: esta vía no lo puede cambiar.
/// </summary>
public interface IAjusteSegundoFactorPorPlataforma
{
    /// <summary>Fija la exigencia de segundo factor del Tenant objetivo de la sesión que se nombra.</summary>
    Task<Result> CambiarAsync(
        Guid sesionPrivilegiadaId, bool exigeSegundoFactorTodosLosPerfiles, string motivo,
        CancellationToken cancellationToken);
}
