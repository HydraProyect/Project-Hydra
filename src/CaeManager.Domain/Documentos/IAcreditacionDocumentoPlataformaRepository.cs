namespace CaeManager.Domain.Documentos;

public interface IAcreditacionDocumentoPlataformaRepository
{
    Task<AcreditacionDocumentoPlataforma?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Todas las acreditaciones de un Documento — usado al renovarlo (invariante: renovar reinicia todas sus acreditaciones a Pendiente de subir).</summary>
    Task<IReadOnlyList<AcreditacionDocumentoPlataforma>> ObtenerPorDocumentoIdAsync(Guid documentoId, CancellationToken cancellationToken = default);

    void Agregar(AcreditacionDocumentoPlataforma acreditacion);
}
