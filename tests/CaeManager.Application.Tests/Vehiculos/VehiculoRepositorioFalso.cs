using CaeManager.Domain.Vehiculos;

namespace CaeManager.Application.Tests.Vehiculos;

/// <summary>Fake en memoria — los handlers de Application se prueban sin base de datos (ver CODING_STANDARDS.md).</summary>
public class VehiculoRepositorioFalso : IVehiculoRepository
{
    public List<Vehiculo> Vehiculos { get; } = [];

    public Task<Vehiculo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Vehiculos.FirstOrDefault(v => v.Id == id));

    public Task<bool> ExisteConMatriculaAsync(string numeroPlaca, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Vehiculos.Any(v => v.NumeroPlaca == numeroPlaca && v.Id != excluirId));

    public void Agregar(Vehiculo vehiculo) => Vehiculos.Add(vehiculo);
}
