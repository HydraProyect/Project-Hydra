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
    /// Guarda <paramref name="intento"/>, que tiene que ser lo único pendiente del contexto: si hay cualquier otro cambio
    /// pendiente, no guarda nada, retira el intento y lanza <see cref="InvalidOperationException"/>, para que el guardado
    /// bajo el rol elevado no arrastre escrituras de otro acto ni consuma su auditoría. Devuelve <c>false</c>, sin
    /// propagar la excepción, solo si la base rechaza la fila porque el Tenant beneficiario ya tiene un Producto
    /// Contratado de la línea Inbound vigente; cualquier otro error se propaga. Al devolver <c>false</c> retira el
    /// intento, por referencia, y la auditoría que generó.
    /// </summary>
    Task<bool> GuardarDetectandoInboundVigenteDuplicadoAsync(
        ProductoContratado intento, CancellationToken cancellationToken = default);
}
