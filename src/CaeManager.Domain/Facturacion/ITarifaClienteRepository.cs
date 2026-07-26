namespace CaeManager.Domain.Facturacion;

public interface ITarifaClienteRepository
{
    Task<TarifaCliente?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<TarifaCliente>> ObtenerPorClienteAsync(Guid clienteId, CancellationToken cancellationToken = default);
    Task<bool> ExisteParaConceptoAsync(Guid clienteId, ConceptoFacturable concepto, Guid? excluirId = null, CancellationToken cancellationToken = default);
    void Agregar(TarifaCliente tarifa);
    void Eliminar(TarifaCliente tarifa);
}
