using CaeManager.Application.Common;
using CaeManager.Domain.Notificaciones;
using MediatR;

namespace CaeManager.Application.Notificaciones.Queries.ObtenerNotificacionesPendientes;

public record NotificacionDto(Guid Id, string Titulo, string Mensaje, string? UrlAccion, string? TextoAccion);

public record ObtenerNotificacionesPendientesQuery : IRequest<IReadOnlyList<NotificacionDto>>;

public class ObtenerNotificacionesPendientesQueryHandler(
    INotificacionUsuarioRepository repositorio, ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerNotificacionesPendientesQuery, IReadOnlyList<NotificacionDto>>
{
    public async Task<IReadOnlyList<NotificacionDto>> Handle(ObtenerNotificacionesPendientesQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return [];

        var notificaciones = await repositorio.ObtenerPendientesPorUsuarioAsync(usuarioId.Value, cancellationToken);
        return notificaciones.Select(n => new NotificacionDto(n.Id, n.Titulo, n.Mensaje, n.UrlAccion, n.TextoAccion)).ToList();
    }
}
