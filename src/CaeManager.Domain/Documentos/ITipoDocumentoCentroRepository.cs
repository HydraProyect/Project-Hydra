namespace CaeManager.Domain.Documentos;

public interface ITipoDocumentoCentroRepository
{
    Task<TipoDocumentoCentro?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TipoDocumentoCentro>> ObtenerPorTipoDocumentoAsync(Guid tipoDocumentoId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TipoDocumentoCentro>> ObtenerPorCentroAsync(Guid centroId, CancellationToken cancellationToken = default);

    Task<TipoDocumentoCentro?> ObtenerPorParAsync(Guid tipoDocumentoId, Guid centroId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Como <see cref="ObtenerPorParAsync"/>, pero con lo confirmado en la base aunque el contexto ya rastreara la fila:
    /// para leer bajo el candado de las altas con acreditación (revisión Codex de la PR #2074, ronda 4, hallazgo 2).
    /// </summary>
    Task<TipoDocumentoCentro?> ObtenerPorParActualizadoAsync(Guid tipoDocumentoId, Guid centroId, CancellationToken cancellationToken = default);

    /// <summary>Como <see cref="ObtenerPorTipoDocumentoAsync"/>, con lo confirmado en la base.</summary>
    Task<IReadOnlyList<TipoDocumentoCentro>> ObtenerPorTipoDocumentoActualizadoAsync(Guid tipoDocumentoId, CancellationToken cancellationToken = default);

    void Agregar(TipoDocumentoCentro tipoDocumentoCentro);

    void Eliminar(TipoDocumentoCentro tipoDocumentoCentro);
}
