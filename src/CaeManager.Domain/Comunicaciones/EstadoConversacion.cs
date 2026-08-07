namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Estado de negocio de una Conversacion dentro de la bandeja
/// compartida (ver ARQUITECTURA-INTEGRACIONES.md § 12). Abierta es el valor
/// por defecto al crear un hilo nuevo.
/// </summary>
public enum EstadoConversacion
{
    Abierta,
    Pendiente,
    Resuelta,
    Cerrada
}
