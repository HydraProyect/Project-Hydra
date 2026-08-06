namespace CaeManager.Domain.Documentos;

public interface ITipoDocumentoCentroRepository
{
    Task<TipoDocumentoCentro?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TipoDocumentoCentro>> ObtenerPorTipoDocumentoAsync(Guid tipoDocumentoId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TipoDocumentoCentro>> ObtenerPorCentroAsync(Guid centroId, CancellationToken cancellationToken = default);

    Task<TipoDocumentoCentro?> ObtenerPorParAsync(Guid tipoDocumentoId, Guid centroId, CancellationToken cancellationToken = default);

    void Agregar(TipoDocumentoCentro tipoDocumentoCentro);

    void Eliminar(TipoDocumentoCentro tipoDocumentoCentro);
}
