namespace CaeManager.Domain.Documentos;

public interface IDocumentoRepository
{
    Task<Documento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(Documento documento);
}
