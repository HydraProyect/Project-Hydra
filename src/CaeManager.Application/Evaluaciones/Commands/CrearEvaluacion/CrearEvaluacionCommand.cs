using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Evaluaciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Evaluaciones.Commands.CrearEvaluacion;

public record CrearEvaluacionCommand(
    Guid CentroId, Guid? TrabajadorId, DateOnly Fecha, int Puntuacion, string? Observaciones) : ICommand<Guid>;

public class CrearEvaluacionCommandValidator : AbstractValidator<CrearEvaluacionCommand>
{
    public CrearEvaluacionCommandValidator()
    {
        RuleFor(c => c.CentroId).NotEmpty().WithMessage("Selecciona un centro.");
        RuleFor(c => c.Puntuacion).InclusiveBetween(Evaluacion.PuntuacionMinima, Evaluacion.PuntuacionMaxima);
        RuleFor(c => c.Observaciones).MaximumLength(Evaluacion.LongitudMaximaObservaciones);
    }
}

public class CrearEvaluacionCommandHandler(IEvaluacionRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearEvaluacionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearEvaluacionCommand request, CancellationToken cancellationToken)
    {
        var evaluacion = new Evaluacion(request.CentroId, request.TrabajadorId, request.Fecha, request.Puntuacion, request.Observaciones);
        repositorio.Agregar(evaluacion);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(evaluacion.Id);
    }
}
