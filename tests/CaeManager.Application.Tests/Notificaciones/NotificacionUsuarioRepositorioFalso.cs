using CaeManager.Domain.Notificaciones;

namespace CaeManager.Application.Tests.Notificaciones;

public class NotificacionUsuarioRepositorioFalso : INotificacionUsuarioRepository
{
    public List<NotificacionUsuario> Notificaciones { get; } = [];

    public Task<NotificacionUsuario?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Notificaciones.FirstOrDefault(n => n.Id == id));

    public Task<IReadOnlyList<NotificacionUsuario>> ObtenerPendientesPorUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NotificacionUsuario>>(
            Notificaciones.Where(n => n.UsuarioDestinatarioId == usuarioId && !n.Leida).ToList());

    public void Agregar(NotificacionUsuario notificacion) => Notificaciones.Add(notificacion);
}
