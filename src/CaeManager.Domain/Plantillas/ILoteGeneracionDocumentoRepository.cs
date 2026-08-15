namespace CaeManager.Domain.Plantillas;

public interface ILoteGeneracionDocumentoRepository
{
    void Agregar(LoteGeneracionDocumento lote);

    Task<LoteGeneracionDocumento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);
}
