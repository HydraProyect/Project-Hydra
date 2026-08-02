using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class EventoWebhookRepository(CaeManagerDbContext dbContext) : IEventoWebhookRepository
{
    public Task<EventoWebhook?> ObtenerSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
        dbContext.EventosWebhook
            .Where(e => !e.Procesado)
            .OrderBy(e => e.FechaRecepcionUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public void Agregar(EventoWebhook evento) => dbContext.EventosWebhook.Add(evento);
}
