namespace CaeManager.Domain.Comunicaciones;

public interface IClasificacionRelevanciaCaeRepository
{
    void Agregar(ClasificacionRelevanciaCae clasificacion);

    Task<ClasificacionRelevanciaCae?> ObtenerPorConversacionIdAsync(Guid conversacionId, CancellationToken cancellationToken = default);
}
