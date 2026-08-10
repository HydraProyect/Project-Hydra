namespace CaeManager.Domain.Comunicaciones;

public interface IDetalleSugerenciaGestionCorreoRepository
{
    Task<DetalleSugerenciaGestionCorreo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);
}
