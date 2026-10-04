namespace CaeManager.Domain.Visitas;

public interface IVisitaRepository
{
    Task<Visita?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Igual que <see cref="ObtenerPorIdAsync"/>, pero con los valores releídos de la base de datos
    /// aunque el contexto ya rastreara la entidad. Hace falta cuando el handler COMPARA contra el
    /// estado actual: el contexto de un circuito de Blazor es de vida larga, y una instancia
    /// rastreada antes en el mismo circuito oculta lo que otra persona hizo después.
    /// </summary>
    Task<Visita?> ObtenerPorIdActualizadoAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(Visita visita);
}
