namespace CaeManager.Domain.Visitas;

public interface IVisitaRepository
{
    Task<Visita?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(Visita visita);
}
