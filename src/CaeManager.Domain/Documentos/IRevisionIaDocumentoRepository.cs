namespace CaeManager.Domain.Documentos;

public interface IRevisionIaDocumentoRepository
{
    Task<RevisionIaDocumento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(RevisionIaDocumento revision);
}
