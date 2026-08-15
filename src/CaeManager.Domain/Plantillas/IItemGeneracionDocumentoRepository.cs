namespace CaeManager.Domain.Plantillas;

public interface IItemGeneracionDocumentoRepository
{
    void Agregar(ItemGeneracionDocumento item);

    Task<ItemGeneracionDocumento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);
}
