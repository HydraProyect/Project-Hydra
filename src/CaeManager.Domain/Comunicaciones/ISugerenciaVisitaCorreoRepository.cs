namespace CaeManager.Domain.Comunicaciones;

public interface ISugerenciaVisitaCorreoRepository
{
    Task<SugerenciaVisitaCorreo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(SugerenciaVisitaCorreo sugerencia);
}
