using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Evaluaciones;
using MediatR;

namespace CaeManager.Application.Evaluaciones.Commands.EliminarEvaluacion;

public record EliminarEvaluacionCommand(Guid Id, Guid UsuarioId) : IRequest<Result>;

public class EliminarEvaluacionCommandHandler(IEvaluacionRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarEvaluacionCommand, Result>
{
    public async Task<Result> Handle(EliminarEvaluacionCommand request, CancellationToken cancellationToken)
    {
        var evaluacion = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (evaluacion is null)
            return Result.Fallo(Error.Crear("Evaluacion.NoEncontrada", "No encontramos esta evaluación."));

        evaluacion.MarcarComoEliminado(request.UsuarioId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
