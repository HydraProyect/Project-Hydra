namespace CaeManager.Domain.Comercial;

/// <summary>
/// Línea comercial de un <see cref="ProductoContratado"/> (RQ-30, D-6). En la base se guarda el nombre del
/// valor.
/// <para>
/// La primera versión del alta autónoma vende únicamente la línea Outbound con gestión propia (P, DA-1). La
/// línea Inbound se abre «a presupuesto», sin precio de catálogo (D-1, 2026-11-14): existe para que el gate
/// comercial DN-7 tenga qué comprobar, y ningún importe ni tarifa depende de ella. La gestión externa (E) y la
/// cartera de un Operador CAE externo (O) entran cuando se decida cómo se cobran; añadir un valor exige ampliar
/// el CHECK de la columna en una migración.
/// </para>
/// </summary>
public enum LineaProductoContratado
{
    /// <summary>P: Outbound con gestión propia; el Tenant beneficiario gestiona su CAE con su propio personal.</summary>
    OutboundGestionPropia,

    /// <summary>
    /// Inbound «a presupuesto» (D-1): el Tenant beneficiario, el del Cliente empresarial, valida la documentación de
    /// sus Empresas contratistas. Con un Producto Contratado de esta línea vigente, y solo entonces, las funciones de
    /// lectura entre Tenants <c>app_inbound_*</c> responden a ese Tenant (DN-7, opción (a)); sin él devuelven 0 filas.
    /// El Pagador TALVEG no cuenta: el gate mira el Tenant beneficiario, que es el <c>TenantId</c> de la fila.
    /// </summary>
    Inbound,
}
