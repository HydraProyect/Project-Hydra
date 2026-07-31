namespace CaeManager.Domain.Tenants;

public interface IAsignacionOperadorDelegadoRepository
{
    Task<AsignacionOperadorDelegado?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExisteAsync(Guid delegacionTenantId, Guid usuarioId, CancellationToken cancellationToken = default);

    Task<AsignacionOperadorDelegado?> ObtenerPorDelegacionYUsuarioAsync(
        Guid delegacionTenantId, Guid usuarioId, CancellationToken cancellationToken = default);

    void Agregar(AsignacionOperadorDelegado asignacion);

    /// <summary>
    /// Retirar a un operador concreto de una delegación sí es un borrado
    /// físico, a diferencia de <c>DelegacionTenant.Desactivar()</c>: la fila
    /// no es el histórico de la relación entre las dos organizaciones (eso lo
    /// conserva la delegación, ADR-004 § 5.5), sino la autorización viva de
    /// una persona. Quién operó qué y cuándo lo registra la auditoría, no
    /// esta tabla.
    /// </summary>
    void Eliminar(AsignacionOperadorDelegado asignacion);
}
