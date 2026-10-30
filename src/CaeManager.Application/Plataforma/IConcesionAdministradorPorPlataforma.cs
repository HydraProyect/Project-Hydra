using CaeManager.Domain.Common;

namespace CaeManager.Application.Plataforma;

/// <summary>
/// La escritura de <c>ConcederAdministradorPorPlataformaCommand</c>: la conexión de una Sesión
/// Privilegiada es de solo lectura, y <c>app_conceder_administrador_por_plataforma</c> es su única
/// puerta para este acto. La función repite cada precondición y audita en la misma transacción.
/// </summary>
public interface IConcesionAdministradorPorPlataforma
{
    /// <summary>Concede Administrador a la cuenta, amparado por la sesión que se nombra.</summary>
    Task<Result> ConcederAsync(
        Guid sesionPrivilegiadaId, Guid usuarioId, string motivo, CancellationToken cancellationToken);
}
