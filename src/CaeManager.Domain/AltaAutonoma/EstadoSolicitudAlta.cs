namespace CaeManager.Domain.AltaAutonoma;

/// <summary>
/// Estados de una <see cref="SolicitudAlta"/>. Las transiciones permitidas las fijan los métodos de la
/// Solicitud de alta; cualquier otra lanza <see cref="TransicionSolicitudAltaNoPermitidaException"/>.
/// <code>
/// Iniciada → CorreoVerificado → DatosCompletos → PendientePago ⟲ → Pagada → Completada
///                     └→ DerivadaAAcceso                               └→ Reembolsada
/// Caducada: desde Iniciada, CorreoVerificado, DatosCompletos o PendientePago, cuando vence el plazo.
/// Bloqueada: desde cualquier estado no final.
/// </code>
/// Finales: DerivadaAAcceso, Completada, Reembolsada, Caducada y Bloqueada.
/// </summary>
public enum EstadoSolicitudAlta
{
    Iniciada,
    CorreoVerificado,
    DerivadaAAcceso,
    DatosCompletos,
    PendientePago,
    Pagada,
    Completada,
    Reembolsada,
    Caducada,
    Bloqueada,
}
