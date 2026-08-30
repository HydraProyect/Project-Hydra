using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Incidencias;
using MediatR;

namespace CaeManager.Application.Incidencias.Commands.EliminarIncidencia;

public record EliminarIncidenciaCommand(Guid Id) : ICommand;

public class EliminarIncidenciaCommandHandler(
    IIncidenciaRepository repositorio, IUnitOfWork unitOfWork, ICurrentUserService currentUserService)
    : IRequestHandler<EliminarIncidenciaCommand, Result>
{
    public async Task<Result> Handle(EliminarIncidenciaCommand request, CancellationToken cancellationToken)
    {
        var incidencia = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (incidencia is null)
            return Result.Fallo(Error.Crear("Incidencia.NoEncontrada", "No encontramos esta incidencia."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("Incidencia.SinIdentidad", "No se pudo confirmar tu identidad. Vuelve a iniciar sesión e inténtalo de nuevo."));

        incidencia.MarcarComoEliminado(usuarioId.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
