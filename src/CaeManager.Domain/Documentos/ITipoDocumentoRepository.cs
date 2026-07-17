namespace CaeManager.Domain.Documentos;

public interface ITipoDocumentoRepository
{
    Task<TipoDocumento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExisteConNombreAsync(string nombre, Guid? excluirId = null, CancellationToken cancellationToken = default);

    void Agregar(TipoDocumento tipoDocumento);
}
