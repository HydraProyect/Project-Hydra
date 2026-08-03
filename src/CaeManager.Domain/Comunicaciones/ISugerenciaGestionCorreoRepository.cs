namespace CaeManager.Domain.Comunicaciones;

public interface ISugerenciaGestionCorreoRepository
{
    Task<SugerenciaGestionCorreo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(SugerenciaGestionCorreo sugerencia);
}
