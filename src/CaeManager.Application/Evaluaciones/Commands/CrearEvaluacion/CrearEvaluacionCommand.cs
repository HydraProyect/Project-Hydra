using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Evaluaciones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

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

public class CrearEvaluacionCommandHandler(
    IEvaluacionRepository repositorio, ICentrosQueryContext centrosContext, ITrabajadoresQueryContext trabajadoresContext, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearEvaluacionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearEvaluacionCommand request, CancellationToken cancellationToken)
    {
        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        if (!await centrosContext.Centros.AnyAsync(c => c.Id == request.CentroId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Evaluacion.CentroNoEncontrado", "No encontramos este centro."));

        if (request.TrabajadorId is { } trabajadorId
            && !await trabajadoresContext.Trabajadores.AnyAsync(t => t.Id == trabajadorId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Evaluacion.TrabajadorNoEncontrado", "No encontramos este trabajador."));

        var evaluacion = new Evaluacion(request.CentroId, request.TrabajadorId, request.Fecha, request.Puntuacion, request.Observaciones);
        repositorio.Agregar(evaluacion);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(evaluacion.Id);
    }
}
