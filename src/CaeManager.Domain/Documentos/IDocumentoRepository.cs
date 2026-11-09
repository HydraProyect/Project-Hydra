namespace CaeManager.Domain.Documentos;

public interface IDocumentoRepository
{
    Task<Documento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pregunta a la base, no al contexto, si hay un Documento con este Id en el Tenant propietario, también eliminado
    /// (una fila eliminada es restaurable y conserva su archivo). Sin Tenant propietario resuelto no se puede comprobar y
    /// responde que sí: quien lo usa para decidir si borra un archivo debe quedarse, ante la duda, sin borrarlo.
    /// </summary>
    Task<bool> PodriaExistirAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(Documento documento);
}
