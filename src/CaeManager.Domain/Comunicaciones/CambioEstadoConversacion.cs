using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Un cambio de estado de una Conversacion y el instante en que ocurrió (decisión de producto 2026-10-17, hueco 2
/// de Comunicaciones). Es la base de las métricas de tiempo de Comunicaciones (hueco 1): cuánto estuvo un hilo
/// esperando al Cliente, esperando a un tercero o abierto se calcula restando instantes consecutivos.
///
/// <b>No guarda quién lo hizo, a propósito.</b> La regla de producto es no fiscalizar al Gestor CAE: la medida
/// es del hilo, no de la persona. No hay campo de usuario; el registro es de solo anexado — nada lo modifica
/// ni lo borra salvo la cascada de su Conversacion.
/// </summary>
public class CambioEstadoConversacion : EntidadConTenant
{
    public Guid ConversacionId { get; private set; }
    public EstadoConversacion EstadoAnterior { get; private set; }
    public EstadoConversacion EstadoNuevo { get; private set; }
    public DateTime FechaUtc { get; private set; }

    private CambioEstadoConversacion()
    {
    }

    public CambioEstadoConversacion(
        Guid conversacionId, EstadoConversacion estadoAnterior, EstadoConversacion estadoNuevo, DateTime fechaUtc)
    {
        if (conversacionId == Guid.Empty)
            throw new ArgumentException("El cambio de estado debe pertenecer a una conversación.", nameof(conversacionId));

        ConversacionId = conversacionId;
        EstadoAnterior = estadoAnterior;
        EstadoNuevo = estadoNuevo;
        FechaUtc = fechaUtc;
    }
}
