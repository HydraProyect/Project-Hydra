namespace CaeManager.Domain.Operaciones;

/// <summary>
/// Ciclo de vida de una <see cref="PropuestaApoyoCartera"/>. Solo
/// <see cref="Pendiente"/> admite cambio; los demás son finales.
/// </summary>
public enum EstadoPropuestaApoyoCartera
{
    /// <summary>Espera a que el Gestor CAE destinatario la acepte o la rechace. No concede nada.</summary>
    Pendiente = 0,

    /// <summary>El destinatario la aceptó y se emitió su Asignación de Cartera de apoyo.</summary>
    Aceptada = 1,

    /// <summary>El destinatario la rechazó. No se emitió nada.</summary>
    Rechazada = 2,

    /// <summary>Quien la propuso la retiró antes de que el destinatario respondiera.</summary>
    Retirada = 3,

    /// <summary>
    /// Se cerró sin respuesta porque dejó de tener sentido al ir a aceptarla. Ver
    /// <see cref="MotivoAnulacionPropuestaApoyo"/>.
    /// </summary>
    Anulada = 4,
}
