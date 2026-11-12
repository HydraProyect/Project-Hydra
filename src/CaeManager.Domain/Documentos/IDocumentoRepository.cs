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

    /// <summary>
    /// El Documento del Tenant indicado aunque esté eliminado (Papelera), releído de la base si el contexto ya lo
    /// rastreaba: quien decide bajo el candado de la regla A-1 tiene que ver lo que otra operación confirmó mientras
    /// esperaba.
    /// </summary>
    Task<Documento?> ObtenerIncluidoEliminadoActualizadoAsync(Guid id, Guid? tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// El Documento del Tenant indicado aunque esté eliminado, leído de la base sin rastrear: una instancia nueva aunque el
    /// contexto ya rastree ese Documento, también con cambios pendientes. Para quien solo decide con él y no lo escribe;
    /// lo que se haga sobre esta instancia no se guarda.
    /// </summary>
    Task<Documento?> ObtenerIncluidoEliminadoSinRastrearAsync(Guid id, Guid? tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bloquea la fila del Documento (<c>FOR SHARE</c>) hasta el final de la transacción en curso y lo devuelve leído de la
    /// base sin rastrear, como <see cref="ObtenerIncluidoEliminadoSinRastrearAsync"/>. Lo que devuelve es lo último
    /// confirmado y nadie puede cambiarlo hasta que la transacción termine: quien escribe algo que depende de su estado sin
    /// escribir el Documento (y por eso sin su token de concurrencia) decide con esto antes de confirmar. Exige una
    /// transacción abierta: sin ella el bloqueo no duraría nada y lanza.
    /// </summary>
    Task<Documento?> BloquearYObtenerIncluidoEliminadoSinRastrearAsync(Guid id, Guid? tenantId, CancellationToken cancellationToken = default);

    void Agregar(Documento documento);
}
