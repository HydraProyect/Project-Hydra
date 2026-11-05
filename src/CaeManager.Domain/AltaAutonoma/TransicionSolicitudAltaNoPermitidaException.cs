namespace CaeManager.Domain.AltaAutonoma;

/// <summary>Una <see cref="SolicitudAlta"/> no admite la transición pedida desde su estado actual.</summary>
public sealed class TransicionSolicitudAltaNoPermitidaException(EstadoSolicitudAlta desde, string transicion)
    : InvalidOperationException($"La Solicitud de alta en estado {desde} no admite «{transicion}».")
{
    public EstadoSolicitudAlta Desde { get; } = desde;

    public string Transicion { get; } = transicion;
}
