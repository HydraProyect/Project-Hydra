namespace CaeManager.Domain.Documentos;

/// <summary>
/// Escritura del historial de presentaciones de un Documento a un Centro (<see cref="PresentacionDocumentoEnCentro"/>).
/// Solo se añade; la lectura para decidir un estado va por <c>IDocumentosQueryContext</c>.
/// </summary>
public interface IPresentacionDocumentoEnCentroRepository
{
    /// <summary>
    /// ¿Ya existe una presentación del mismo Documento al mismo Centro, el mismo día y con el mismo origen? Es la clave de
    /// idempotencia: marcar subida dos veces el mismo día no escribe dos filas.
    /// </summary>
    Task<bool> ExisteAsync(
        Guid documentoId, Guid centroId, DateOnly fechaPresentacion, OrigenPresentacionDocumentoEnCentro origen,
        CancellationToken cancellationToken = default);

    void Agregar(PresentacionDocumentoEnCentro presentacion);
}
