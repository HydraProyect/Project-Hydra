namespace CaeManager.Domain.Gestiones;

public interface IGestionRepository
{
    Task<Gestion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(Gestion gestion);
}
