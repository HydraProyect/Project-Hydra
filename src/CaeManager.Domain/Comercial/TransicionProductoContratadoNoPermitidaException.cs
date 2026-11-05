namespace CaeManager.Domain.Comercial;

/// <summary>Un <see cref="ProductoContratado"/> no admite la transición pedida desde su estado actual.</summary>
public sealed class TransicionProductoContratadoNoPermitidaException(EstadoProductoContratado desde, string transicion)
    : InvalidOperationException($"El Producto Contratado en estado {desde} no admite «{transicion}».")
{
    public EstadoProductoContratado Desde { get; } = desde;

    public string Transicion { get; } = transicion;
}
