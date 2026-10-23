namespace CaeManager.Domain.Notificaciones;

public interface INotificacionUsuarioRepository
{
    Task<NotificacionUsuario?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificacionUsuario>> ObtenerPendientesPorUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>Las notificaciones del Tenant actual, de cualquier destinatario, emitidas por un mismo evento de origen.</summary>
    Task<IReadOnlyList<NotificacionUsuario>> ObtenerPorEventoOrigenAsync(Guid eventoOrigenId, CancellationToken cancellationToken = default);

    void Agregar(NotificacionUsuario notificacion);
}
