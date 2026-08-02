using CaeManager.Domain.Notificaciones;

namespace CaeManager.Application.Notificaciones;

public interface INotificacionesQueryContext
{
    IQueryable<NotificacionUsuario> NotificacionesUsuario { get; }
}
