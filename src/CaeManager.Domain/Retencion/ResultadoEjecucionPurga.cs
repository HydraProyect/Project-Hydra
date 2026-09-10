namespace CaeManager.Domain.Retencion;

/// <summary>
/// Resultado de UNA ejecución de <see cref="SolicitudPurga"/>, distinto de
/// <see cref="SolicitudPurga.Estado"/>: <see cref="EstadoSolicitudPurga.Ejecutada"/>
/// significa que el proceso de ejecución terminó, no que todos los candidatos
/// se suprimieran. Este es el eje que responde a esa segunda pregunta,
/// persistido junto al resultado para que sea trazable y no dependa de una
/// alerta operativa que puede perderse o resolverse sin más.
/// </summary>
public enum ResultadoEjecucionPurga
{
    /// <summary>Se suprimieron todos los candidatos de la ejecución.</summary>
    Completa,

    /// <summary>Al menos un candidato no pudo suprimirse — ver <see cref="IncidenciaPurga"/>.</summary>
    ConIncidencias
}
