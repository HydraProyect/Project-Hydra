namespace CaeManager.Domain.Comercial;

/// <summary>
/// Línea comercial de un <see cref="ProductoContratado"/> (RQ-30, D-6). En la base se guarda el nombre del
/// valor.
/// <para>
/// Solo existe P: la primera versión del alta autónoma vende únicamente la línea Outbound con gestión
/// propia (DA-1). La gestión externa (E), la cartera de un Operador CAE externo (O) e Inbound entran
/// cuando se decida cómo se cobran; añadir un valor exige ampliar el CHECK de la columna en una migración.
/// </para>
/// </summary>
public enum LineaProductoContratado
{
    /// <summary>P: Outbound con gestión propia; el Tenant beneficiario gestiona su CAE con su propio personal.</summary>
    OutboundGestionPropia,
}
