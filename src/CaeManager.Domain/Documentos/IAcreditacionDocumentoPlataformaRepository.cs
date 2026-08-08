namespace CaeManager.Domain.Documentos;

public interface IAcreditacionDocumentoPlataformaRepository
{
    Task<AcreditacionDocumentoPlataforma?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(AcreditacionDocumentoPlataforma acreditacion);
}
