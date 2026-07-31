namespace CaeManager.Domain.Retencion;

/// <summary>
/// Ciclo de vida de una <see cref="SolicitudPurga"/>. El orden importa: la
/// destrucción de datos nunca es un efecto automático de que venza un plazo,
/// sino el último paso de un procedimiento que alguien tiene que autorizar
/// expresamente.
/// </summary>
public enum EstadoSolicitudPurga
{
    /// <summary>
    /// El barrido detectó datos que ya cumplieron su plazo de retención. No
    /// se ha tocado nada: solo se ha levantado la mano.
    /// </summary>
    PendienteDeRevision,

    /// <summary>
    /// Se ha comunicado al tenant que sus datos van a destruirse, para que
    /// pueda extraerlos si su propia política interna exige conservarlos más
    /// tiempo. Es el motivo de que este flujo no sea automático.
    /// </summary>
    TenantAvisado,

    /// <summary>
    /// Autorizada, con fecha de ejecución fijada. Sigue sin destruirse nada
    /// hasta que llegue esa fecha, y puede cancelarse hasta entonces.
    /// </summary>
    Programada,

    /// <summary>Ejecutada. Estado final: no se vuelve atrás.</summary>
    Ejecutada,

    /// <summary>
    /// Descartada — el tenant pidió conservar los datos, cambió el plazo, o
    /// la revisión concluyó que no procedía. Estado final.
    /// </summary>
    Cancelada
}
