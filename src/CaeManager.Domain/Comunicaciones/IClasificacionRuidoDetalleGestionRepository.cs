namespace CaeManager.Domain.Comunicaciones;

public interface IClasificacionRuidoDetalleGestionRepository
{
    void Agregar(ClasificacionRuidoDetalleGestion clasificacion);

    Task<ClasificacionRuidoDetalleGestion?> ObtenerPorDetalleIdAsync(Guid detalleSugerenciaGestionCorreoId, CancellationToken cancellationToken = default);
}
