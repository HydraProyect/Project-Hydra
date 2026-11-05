namespace CaeManager.Domain.Comercial;

/// <summary>
/// Estado contractual de un <see cref="ProductoContratado"/>: si lo contratado sigue en vigor. No es el
/// estado de cobro, que sigue siendo <see cref="Tenants.Tenant.EstadoComercial"/> (ADR-009): un Producto
/// Contratado <see cref="Vigente"/> convive con cualquier <see cref="Tenants.EstadoComercialTenant"/>, y el
/// gate comercial no lo lee. En la base se guarda el nombre del valor.
/// </summary>
public enum EstadoProductoContratado
{
    /// <summary>En vigor desde <see cref="ProductoContratado.VigenteDesdeUtc"/>, sin fecha de fin.</summary>
    Vigente,

    /// <summary>Terminado en <see cref="ProductoContratado.VigenteHastaUtc"/>. Estado final.</summary>
    Finalizado,
}
