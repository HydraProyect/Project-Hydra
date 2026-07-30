namespace CaeManager.Domain.Incidencias;

public interface IIncidenciaRepository
{
    Task<Incidencia?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(Incidencia incidencia);
}
