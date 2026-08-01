using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Proyectos;
using MediatR;

namespace CaeManager.Application.Proyectos.Commands.EliminarProyecto;

public record EliminarProyectoCommand(Guid Id) : ICommand;

public class EliminarProyectoCommandHandler(
    IProyectoRepository repositorio, IUnitOfWork unitOfWork, ICurrentUserService currentUserService)
    : IRequestHandler<EliminarProyectoCommand, Result>
{
    public async Task<Result> Handle(EliminarProyectoCommand request, CancellationToken cancellationToken)
    {
        var proyecto = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (proyecto is null)
            return Result.Fallo(Error.Crear("Proyecto.NoEncontrado", "El proyecto no existe o no tienes acceso."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        proyecto.MarcarComoEliminado(usuarioId ?? Guid.Empty);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
