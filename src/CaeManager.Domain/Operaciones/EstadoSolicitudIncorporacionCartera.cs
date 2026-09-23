namespace CaeManager.Domain.Operaciones;

/// <summary>
/// Ciclo de vida de una <see cref="SolicitudIncorporacionCartera"/>. Solo
/// <see cref="Pendiente"/> admite resolución; solo <see cref="Aceptada"/>
/// admite revocación. Los demás son finales.
/// </summary>
public enum EstadoSolicitudIncorporacionCartera
{
    /// <summary>Espera a que un Coordinador CAE del mismo Operador CAE la resuelva. No concede nada.</summary>
    Pendiente = 0,

    /// <summary>Un Coordinador CAE la aceptó y se creó la Asignación de Cartera universal.</summary>
    Aceptada = 1,

    /// <summary>Un Coordinador CAE la rechazó. No se creó nada.</summary>
    Rechazada = 2,

    /// <summary>
    /// La cartera que se obtuvo al aceptarla se retiró, por un Coordinador CAE
    /// del Operador CAE o por el propio Gestor CAE.
    /// </summary>
    Revocada = 3,

    /// <summary>
    /// Se cerró sin que nadie la resolviera, porque dejó de tener sentido: el
    /// solicitante ya tenía el Tenant en su cartera, o la Asignación de
    /// Operación ya no estaba vigente. Ver <see cref="MotivoAnulacionSolicitudCartera"/>.
    /// </summary>
    Anulada = 4,
}
