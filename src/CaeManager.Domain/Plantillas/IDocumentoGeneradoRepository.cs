namespace CaeManager.Domain.Plantillas;

public interface IDocumentoGeneradoRepository
{
    void Agregar(DocumentoGenerado documentoGenerado);

    Task<DocumentoGenerado?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);
}
