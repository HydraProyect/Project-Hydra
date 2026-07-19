namespace CaeManager.Domain.Trabajadores;

public interface IDeteccionTrabajadorRepository
{
    Task<DeteccionTrabajador?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(DeteccionTrabajador deteccion);
}
