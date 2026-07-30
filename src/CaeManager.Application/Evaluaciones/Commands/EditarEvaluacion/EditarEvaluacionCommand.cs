using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Evaluaciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Evaluaciones.Commands.EditarEvaluacion;

public record EditarEvaluacionCommand(
    Guid Id, Guid? TrabajadorId, DateOnly Fecha, int Puntuacion, string? Observaciones) : IRequest<Result>;

public class EditarEvaluacionCommandValidator : AbstractValidator<EditarEvaluacionCommand>
{
    public EditarEvaluacionCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Puntuacion).InclusiveBetween(Evaluacion.PuntuacionMinima, Evaluacion.PuntuacionMaxima);
        RuleFor(c => c.Observaciones).MaximumLength(Evaluacion.LongitudMaximaObservaciones);
    }
}

public class EditarEvaluacionCommandHandler(IEvaluacionRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarEvaluacionCommand, Result>
{
    public async Task<Result> Handle(EditarEvaluacionCommand request, CancellationToken cancellationToken)
    {
        var evaluacion = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (evaluacion is null)
            return Result.Fallo(Error.Crear("Evaluacion.NoEncontrada", "No encontramos esta evaluación."));

        try
        {
            evaluacion.Actualizar(request.TrabajadorId, request.Fecha, request.Puntuacion, request.Observaciones);
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(Error.Crear("Evaluacion.DatosInvalidos", ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
