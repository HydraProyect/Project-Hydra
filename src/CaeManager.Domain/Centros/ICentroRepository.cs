namespace CaeManager.Domain.Centros;

public interface ICentroRepository
{
    Task<Centro?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>El nombre de un Centro es único dentro de su Cliente, no globalmente.</summary>
    Task<bool> ExisteConNombreEnClienteAsync(
        Guid clienteId, string nombre, Guid? excluirId = null, CancellationToken cancellationToken = default);

    void Agregar(Centro centro);
}
