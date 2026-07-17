namespace CaeManager.Domain.Trabajadores;

public interface ITrabajadorRepository
{
    Task<Trabajador?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExisteConDniAsync(string dni, Guid? excluirId = null, CancellationToken cancellationToken = default);

    void Agregar(Trabajador trabajador);
}
