using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Incidencias;
using MediatR;

namespace CaeManager.Application.Incidencias.Commands.EliminarIncidencia;

public record EliminarIncidenciaCommand(Guid Id, Guid UsuarioId) : IRequest<Result>;

public class EliminarIncidenciaCommandHandler(IIncidenciaRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarIncidenciaCommand, Result>
{
    public async Task<Result> Handle(EliminarIncidenciaCommand request, CancellationToken cancellationToken)
    {
        var incidencia = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (incidencia is null)
            return Result.Fallo(Error.Crear("Incidencia.NoEncontrada", "No encontramos esta incidencia."));

        incidencia.MarcarComoEliminado(request.UsuarioId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
