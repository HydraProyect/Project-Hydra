using CaeManager.Domain.Trabajadores;

namespace CaeManager.Application.Tests.Trabajadores;

public class DeteccionTrabajadorRepositorioFalso : IDeteccionTrabajadorRepository
{
    public List<DeteccionTrabajador> Detecciones { get; } = [];

    public Task<DeteccionTrabajador?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Detecciones.FirstOrDefault(d => d.Id == id));

    public void Agregar(DeteccionTrabajador deteccion) => Detecciones.Add(deteccion);
}
