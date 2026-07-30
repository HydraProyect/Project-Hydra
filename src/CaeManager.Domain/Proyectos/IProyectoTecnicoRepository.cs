namespace CaeManager.Domain.Proyectos;

public interface IProyectoTecnicoRepository
{
    Task<ProyectoTecnico?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExisteActivoAsync(Guid proyectoId, Guid trabajadorId, CancellationToken cancellationToken = default);

    void Agregar(ProyectoTecnico proyectoTecnico);
}
