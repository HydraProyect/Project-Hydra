using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ConversacionCorreoRepository(CaeManagerDbContext dbContext) : IConversacionCorreoRepository
{
    public Task<ConversacionCorreo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ConversacionesCorreo
            .Include(c => c.Mensajes)
            .Include(c => c.Participantes)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public void Agregar(ConversacionCorreo conversacion) => dbContext.ConversacionesCorreo.Add(conversacion);
}
