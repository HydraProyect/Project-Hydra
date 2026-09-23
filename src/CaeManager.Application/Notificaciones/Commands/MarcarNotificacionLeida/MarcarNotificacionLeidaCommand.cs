using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Notificaciones;
using MediatR;

namespace CaeManager.Application.Notificaciones.Commands.MarcarNotificacionLeida;

public record MarcarNotificacionLeidaCommand(Guid Id) : ICommand, IComandoDeAutoservicio;

public class MarcarNotificacionLeidaCommandHandler(
    INotificacionUsuarioRepository repositorio, IUnitOfWork unitOfWork, ICurrentUserService currentUserService)
    : IRequestHandler<MarcarNotificacionLeidaCommand, Result>
{
    public async Task<Result> Handle(MarcarNotificacionLeidaCommand request, CancellationToken cancellationToken)
    {
        // Misma respuesta si no existe y si es de otro usuario: un mensaje distinto
        // revelaría que el Id existe (condición de IComandoDeAutoservicio).
        var notificacion = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (notificacion is null || usuarioId is null || notificacion.UsuarioDestinatarioId != usuarioId)
            return Result.Fallo(Error.Crear("Notificacion.NoEncontrada", "No encontramos esta notificación."));

        notificacion.MarcarComoLeida();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
