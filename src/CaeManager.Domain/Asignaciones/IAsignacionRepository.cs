namespace CaeManager.Domain.Asignaciones;

public interface IAsignacionRepository
{
    Task<Asignacion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExisteActivaAsync(Guid trabajadorId, Guid centroId, CancellationToken cancellationToken = default);

    void Agregar(Asignacion asignacion);
}
