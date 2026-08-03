using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ConversacionCorreoRepository(CaeManagerDbContext dbContext) : IConversacionCorreoRepository
{
    public Task<ConversacionCorreo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ConversacionesCorreo
            .Include(c => c.Mensajes).ThenInclude(m => m.Adjuntos)
            .Include(c => c.Participantes)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<ConversacionCorreo?> ObtenerPorHiloExternoAsync(string hiloExternoId, CancellationToken cancellationToken = default) =>
        dbContext.ConversacionesCorreo
            .Include(c => c.Mensajes).ThenInclude(m => m.Adjuntos)
            .Include(c => c.Participantes)
            .FirstOrDefaultAsync(c => c.HiloExternoId == hiloExternoId, cancellationToken);

    public Task<bool> ExisteMensajeExternoAsync(string mensajeExternoId, CancellationToken cancellationToken = default) =>
        dbContext.MensajesCorreo.AnyAsync(m => m.MensajeExternoId == mensajeExternoId, cancellationToken);

    public void Agregar(ConversacionCorreo conversacion) => dbContext.ConversacionesCorreo.Add(conversacion);
}
