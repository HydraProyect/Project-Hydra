namespace CaeManager.Domain.Vehiculos;

public interface IVehiculoRepository
{
    Task<Vehiculo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExisteConMatriculaAsync(string numeroPlaca, Guid? excluirId = null, CancellationToken cancellationToken = default);

    void Agregar(Vehiculo vehiculo);
}
