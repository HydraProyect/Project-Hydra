namespace CaeManager.Domain.Comercial;

/// <summary>
/// Escritura del Producto Contratado. Hoy solo la usa <c>ContratarLineaInboundCommand</c> (B-04a), que corre como
/// <c>cae_app_aprovisionamiento</c>: ese rol tiene INSERT y no SELECT sobre <c>ProductosContratados</c> (decisión D-C,
/// 2026-11-15), así que este puerto no lee. La regla de un solo Producto Contratado de la línea Inbound vigente por
/// Tenant beneficiario (D-B) la impone el índice único parcial de la base y este puerto la traduce.
/// </summary>
public interface IProductoContratadoRepository
{
    void Agregar(ProductoContratado producto);

    /// <summary>
    /// Guarda los cambios pendientes. Devuelve <c>false</c>, sin propagar la excepción, solo si la base rechaza la fila
    /// porque el Tenant beneficiario ya tiene un Producto Contratado de la línea Inbound vigente; cualquier otro error se
    /// propaga. Al devolver <c>false</c> retira solo lo del intento rechazado (el Producto Contratado añadido y su
    /// auditoría): los cambios pendientes de otro acto en el mismo contexto siguen rastreados.
    /// </summary>
    Task<bool> GuardarDetectandoInboundVigenteDuplicadoAsync(CancellationToken cancellationToken = default);
}
