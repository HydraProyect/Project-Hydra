using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class EventoConversacionRepository(CaeManagerDbContext dbContext) : IEventoConversacionRepository
{
    public void Agregar(EventoConversacion evento) => dbContext.EventosConversacion.Add(evento);
}
