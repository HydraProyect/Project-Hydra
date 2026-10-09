namespace CaeManager.Domain.Operaciones;

/// <summary>
/// Ciclo de vida de una <see cref="PropuestaApoyoCartera"/>. <see cref="Pendiente"/> admite
/// las cuatro salidas; <see cref="Aceptada"/> solo pasa a <see cref="Terminada"/>; los demás
/// son finales.
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

    /// <summary>
    /// El apoyo que nació de esta propuesta terminó por retirada: se desasignó el propio Gestor
    /// CAE de apoyo, lo retiró quien lo concedió o lo revocó un Coordinador CAE o un superior.
    /// Un apoyo que termina porque su Asignación de Cartera caduca o cae con la operación no
    /// pasa por aquí: la propuesta se queda en <see cref="Aceptada"/> apuntando a una cartera
    /// cerrada, y quien pregunta «¿sigue vivo este apoyo?» mira siempre la cartera.
    /// </summary>
    Terminada = 5,
}
