namespace CaeManager.Domain.Documentos;

public interface ITipoDocumentoRepository
{
    Task<TipoDocumento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Como <see cref="ObtenerPorIdAsync"/>, pero con lo confirmado en la base aunque el contexto ya rastreara el tipo
    /// (no tiene token de concurrencia): para leer bajo el candado de las altas con acreditación. Relee el tipo, no sus
    /// alias.
    /// </summary>
    Task<TipoDocumento?> ObtenerPorIdActualizadoAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExisteConNombreAsync(string nombre, Guid? excluirId = null, CancellationToken cancellationToken = default);

    void Agregar(TipoDocumento tipoDocumento);
}
