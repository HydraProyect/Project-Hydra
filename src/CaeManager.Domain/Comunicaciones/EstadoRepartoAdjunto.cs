namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Qué se decidió sobre un adjunto de correo al repartirlo. Solo son estados guardados los que resultan de una
/// decisión; si un adjunto pendiente está listo, por revisar o sin destino se deduce de su destino y su tipo.
/// </summary>
public enum EstadoRepartoAdjunto
{
    Pendiente = 0,
    Asignado = 1,
    Rechazado = 2,
    Descartado = 3,

    /// <summary>
    /// El PDF se repartió en tramos y de él salieron varios Documentos. No apunta a ninguno: cada uno queda en el
    /// timeline de la conversación. Se fija antes de crear el primero, para que dos sesiones no dividan el mismo adjunto.
    /// </summary>
    Dividido = 4
}
