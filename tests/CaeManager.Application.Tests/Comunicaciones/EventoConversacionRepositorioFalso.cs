using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>Fake en memoria — mismo criterio que ConversacionRepositorioFalso (ver CODING_STANDARDS.md).</summary>
public class EventoConversacionRepositorioFalso : IEventoConversacionRepository
{
    public List<EventoConversacion> Eventos { get; } = [];

    public void Agregar(EventoConversacion evento) => Eventos.Add(evento);
}
