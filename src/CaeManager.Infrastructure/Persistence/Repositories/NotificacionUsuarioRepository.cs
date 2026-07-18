using CaeManager.Domain.Notificaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class NotificacionUsuarioRepository(CaeManagerDbContext dbContext) : INotificacionUsuarioRepository
{
    public Task<NotificacionUsuario?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.NotificacionesUsuario.FirstOrDefaultAsync(n => n.Id == id, cancellationToken);

    public async Task<IReadOnlyList<NotificacionUsuario>> ObtenerPendientesPorUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        await dbContext.NotificacionesUsuario
            .Where(n => n.UsuarioDestinatarioId == usuarioId && !n.Leida)
            .OrderBy(n => n.CreadaEnUtc)
            .ToListAsync(cancellationToken);

    public void Agregar(NotificacionUsuario notificacion) => dbContext.NotificacionesUsuario.Add(notificacion);
}
