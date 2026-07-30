namespace CaeManager.Domain.Tenants;

public interface IAsignacionOperadorDelegadoRepository
{
    Task<AsignacionOperadorDelegado?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExisteAsync(Guid delegacionTenantId, Guid usuarioId, CancellationToken cancellationToken = default);

    void Agregar(AsignacionOperadorDelegado asignacion);
}
