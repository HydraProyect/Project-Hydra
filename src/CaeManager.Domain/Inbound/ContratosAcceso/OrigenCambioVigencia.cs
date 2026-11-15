namespace CaeManager.Domain.Inbound.ContratosAcceso;

/// <summary>
/// Qué acto cambió el fin contratado de un contrato de acceso: <see cref="Renovacion"/> (Renovar, que solo amplía) o
/// <see cref="Edicion"/> (Actualizar, que puede ampliar o acortar). Se guarda como texto.
/// </summary>
public enum OrigenCambioVigencia
{
    Renovacion,
    Edicion
}
