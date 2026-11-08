namespace CaeManager.Domain.Documentos;

public interface IAcreditacionDocumentoPlataformaRepository
{
    Task<AcreditacionDocumentoPlataforma?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Igual que <see cref="ObtenerPorIdAsync"/>, pero con los valores releídos de la base de datos
    /// aunque el contexto ya rastreara la entidad. Hace falta cuando el handler COMPARA contra el
    /// estado actual (o informa de lo que sobrescribe): el contexto de un circuito de Blazor es de
    /// vida larga, y una instancia rastreada por una anotación anterior del mismo circuito oculta
    /// lo que otra persona hizo después.
    /// </summary>
    Task<AcreditacionDocumentoPlataforma?> ObtenerPorIdActualizadoAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Todas las acreditaciones de un Documento — usado al renovarlo (invariante: renovar reinicia todas sus acreditaciones a Pendiente de subir).</summary>
    Task<IReadOnlyList<AcreditacionDocumentoPlataforma>> ObtenerPorDocumentoIdAsync(Guid documentoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Las acreditaciones del Documento agregadas en esta unidad de trabajo y todavía sin guardar: las que el acto en
    /// curso acaba de hacer nacer. Las ya guardadas no salen, aunque estén rastreadas.
    /// </summary>
    IReadOnlyList<AcreditacionDocumentoPlataforma> AgregadasSinGuardar(Guid documentoId);

    void Agregar(AcreditacionDocumentoPlataforma acreditacion);
}
