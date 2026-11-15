namespace CaeManager.Domain.Comercial;

/// <summary>
/// Escritura del Producto Contratado. Hoy solo la usan los comandos de la línea Inbound (B-04a y su baja o corrección,
/// opción B del 2026-11-15), que corren como <c>cae_app_aprovisionamiento</c>. Ese rol inserta, lee solo las columnas que
/// necesita el WHERE de la baja (<c>Id</c>, <c>TenantId</c>, <c>Linea</c>, <c>Estado</c>, <c>EstaEliminado</c>) y
/// actualiza solo <c>Estado</c> y <c>VigenteHastaUtc</c>, así que este puerto nunca materializa un Producto Contratado
/// leído. La regla de un solo Producto Contratado de la línea Inbound vigente por Tenant beneficiario (D-B) la impone el
/// índice único parcial de la base y este puerto la traduce.
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

    /// <summary>
    /// Finaliza el Producto Contratado de la línea Inbound vigente del Tenant activo y deja su registro de auditoría. El
    /// fin de vigencia es el <c>now()</c> de la transacción de la base, no el reloj de la aplicación: es el mismo instante
    /// con el que la base comprueba el margen de las políticas y con el que el gate DN-7 compara el inicio de una
    /// contratación, así que la corrección puede usarlo como inicio de la nueva sin que un desfase de relojes la deje
    /// fuera del gate (hallazgo 1 de Codex, ronda 1). Lo devuelve en <see cref="FinalizacionInbound.VigenteHastaUtc"/>.
    /// Escribe en el acto, así que el llamador lo envuelve en una transacción si tiene que ir junto con otra escritura. El
    /// contexto no puede tener cambios pendientes: si los tiene, no escribe nada y lanza
    /// <see cref="InvalidOperationException"/>. No pasa por <see cref="ProductoContratado.Finalizar"/> porque el rol
    /// elevado no puede leer la fila entera: la coherencia entre inicio y fin la impone el CHECK
    /// <c>CK_ProductosContratados_EstadoYVigencia</c>, y el resultado
    /// <see cref="ResultadoFinalizacionInbound.VigenciaAunNoIniciada"/> la traduce.
    /// </summary>
    Task<FinalizacionInbound> FinalizarInboundVigenteAsync(CancellationToken cancellationToken = default);
}

/// <summary>Qué hizo <see cref="IProductoContratadoRepository.FinalizarInboundVigenteAsync"/>.</summary>
public enum ResultadoFinalizacionInbound
{
    /// <summary>La fila vigente pasó a Finalizado; <see cref="FinalizacionInbound.ProductoContratadoId"/> la identifica.</summary>
    Finalizada,

    /// <summary>El Tenant activo no tiene un Producto Contratado de la línea Inbound vigente: no se escribió nada.</summary>
    SinInboundVigente,

    /// <summary>
    /// El fin quedaría antes del inicio de la vigencia (una contratación con el reloj de la aplicación adelantado, dentro
    /// del margen de la política de INSERT): no se escribió nada.
    /// </summary>
    VigenciaAunNoIniciada,
}

/// <summary>
/// Resultado de la finalización y, si la hubo, el Producto Contratado finalizado y su fin de vigencia, en UTC.
/// </summary>
public readonly record struct FinalizacionInbound(
    ResultadoFinalizacionInbound Resultado, Guid? ProductoContratadoId, DateTime? VigenteHastaUtc = null);
