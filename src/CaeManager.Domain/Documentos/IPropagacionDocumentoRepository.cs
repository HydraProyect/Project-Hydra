namespace CaeManager.Domain.Documentos;

public interface IPropagacionDocumentoRepository
{
    Task<PropagacionDocumento?> ObtenerAsync(Guid documentoId, Guid canalId, CancellationToken cancellationToken);
    void Agregar(PropagacionDocumento propagacion);
}
