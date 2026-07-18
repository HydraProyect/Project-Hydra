namespace CaeManager.Domain.Notificaciones;

public interface INotificacionUsuarioRepository
{
    Task<NotificacionUsuario?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificacionUsuario>> ObtenerPendientesPorUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    void Agregar(NotificacionUsuario notificacion);
}
