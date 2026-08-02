using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Evaluaciones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Evaluaciones.Commands.EditarEvaluacion;

public record EditarEvaluacionCommand(
    Guid Id, Guid? TrabajadorId, DateOnly Fecha, int Puntuacion, string? Observaciones,
    Guid Version = default) : ICommand;

public class EditarEvaluacionCommandValidator : AbstractValidator<EditarEvaluacionCommand>
{
    public EditarEvaluacionCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Puntuacion).InclusiveBetween(Evaluacion.PuntuacionMinima, Evaluacion.PuntuacionMaxima);
        RuleFor(c => c.Observaciones).MaximumLength(Evaluacion.LongitudMaximaObservaciones);
    }
}

public class EditarEvaluacionCommandHandler(
    IEvaluacionRepository repositorio, IApplicationDbContext dbContext, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarEvaluacionCommand, Result>
{
    public async Task<Result> Handle(EditarEvaluacionCommand request, CancellationToken cancellationToken)
    {
        var evaluacion = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (evaluacion is null)
            return Result.Fallo(Error.Crear("Evaluacion.NoEncontrada", "No encontramos esta evaluación."));

        if (ConcurrenciaOptimista.Verificar(evaluacion, request.Version, "esta evaluación") is { } conflicto)
            return Result.Fallo(conflicto);

        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        if (request.TrabajadorId is { } trabajadorId
            && !await dbContext.Trabajadores.AnyAsync(t => t.Id == trabajadorId, cancellationToken))
            return Result.Fallo(Error.Crear("Evaluacion.TrabajadorNoEncontrado", "No encontramos este trabajador."));

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
