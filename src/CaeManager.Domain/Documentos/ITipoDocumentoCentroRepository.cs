namespace CaeManager.Domain.Documentos;

public interface ITipoDocumentoCentroRepository
{
    Task<IReadOnlyList<TipoDocumentoCentro>> ObtenerPorTipoDocumentoAsync(Guid tipoDocumentoId, CancellationToken cancellationToken = default);

    void Agregar(TipoDocumentoCentro tipoDocumentoCentro);

    void Eliminar(TipoDocumentoCentro tipoDocumentoCentro);
}
