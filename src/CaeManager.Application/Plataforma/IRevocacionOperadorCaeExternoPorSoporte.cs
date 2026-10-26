using CaeManager.Domain.Common;

namespace CaeManager.Application.Plataforma;

/// <summary>
/// Puerta de escritura de la revocación por Soporte TALVEG
/// (<c>RevocarOperadorCaeExternoPorSoporteCommand</c>). La implementación llama a la función
/// <c>app_revocar_operador_cae_externo_por_soporte</c>: la conexión de la Sesión Privilegiada
/// lleva <c>cae_app_soporte</c> (solo <c>SELECT</c>) y esa función es la única que puede
/// escribir, después de repetir en la base la sesión, la concesión, la delegación y el motivo.
/// </summary>
public interface IRevocacionOperadorCaeExternoPorSoporte
{
    /// <summary>
    /// Revoca la Delegación <paramref name="delegacionTenantId"/> del Tenant del contexto
    /// amparada por <paramref name="sesionPrivilegiadaId"/>. Una delegación inexistente, de otro
    /// Tenant o de Soporte da el mismo error.
    /// </summary>
    Task<Result> RevocarAsync(
        Guid sesionPrivilegiadaId, Guid delegacionTenantId, string motivo, CancellationToken cancellationToken);
}
