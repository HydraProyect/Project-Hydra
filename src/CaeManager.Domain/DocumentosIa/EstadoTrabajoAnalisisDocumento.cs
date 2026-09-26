namespace CaeManager.Domain.DocumentosIa;

/// <summary>Estado de un <see cref="TrabajoAnalisisDocumento"/> en la cola durable.</summary>
public enum EstadoTrabajoAnalisisDocumento
{
    Pendiente,
    Procesando,
    Completado,
    Fallido,

    /// <summary>
    /// El análisis llegó tarde: entre el encolado y la escritura, alguien
    /// decidió el Documento a mano o el Documento dejó de ser el que se
    /// encoló (ver <see cref="TrabajoAnalisisDocumento.MarcarDescartado"/>).
    /// No escribió nada sobre el Documento, y no es un fallo: no se reintenta.
    /// </summary>
    Descartado
}
