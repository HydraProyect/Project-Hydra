using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Notificaciones;
using MediatR;

namespace CaeManager.Application.Notificaciones.Commands.MarcarNotificacionLeida;

public record MarcarNotificacionLeidaCommand(Guid Id) : ICommand;

public class MarcarNotificacionLeidaCommandHandler(
    INotificacionUsuarioRepository repositorio, IUnitOfWork unitOfWork, ICurrentUserService currentUserService)
    : IRequestHandler<MarcarNotificacionLeidaCommand, Result>
{
    public async Task<Result> Handle(MarcarNotificacionLeidaCommand request, CancellationToken cancellationToken)
    {
        var notificacion = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (notificacion is null)
            return Result.Fallo(Error.Crear("Notificacion.NoEncontrada", "No encontramos esta notificación."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (notificacion.UsuarioDestinatarioId != usuarioId)
            return Result.Fallo(Error.Crear("Notificacion.SinAcceso", "Esta notificación no es tuya."));

        notificacion.MarcarComoLeida();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
