namespace CaeManager.Domain.Documentos;

public interface IDocumentoRepository
{
    Task<Documento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// El Documento del Tenant indicado aunque esté eliminado (Papelera), releído de la base si el contexto ya lo
    /// rastreaba: quien decide bajo el candado de la regla A-1 tiene que ver lo que otra operación confirmó mientras
    /// esperaba.
    /// </summary>
    Task<Documento?> ObtenerIncluidoEliminadoActualizadoAsync(Guid id, Guid? tenantId, CancellationToken cancellationToken = default);

    void Agregar(Documento documento);
}
