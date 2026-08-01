namespace CaeManager.Domain.DocumentosIa;

/// <summary>Estado de un <see cref="TrabajoAnalisisDocumento"/> en la cola durable.</summary>
public enum EstadoTrabajoAnalisisDocumento
{
    Pendiente,
    Procesando,
    Completado,
    Fallido
}
