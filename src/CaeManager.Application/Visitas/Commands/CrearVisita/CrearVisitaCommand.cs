using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Visitas.Commands.CrearVisita;

public record CrearVisitaCommand(
    Guid CentroId, DateOnly FechaInicio, DateOnly FechaFin, IReadOnlyList<Guid> TrabajadorIds, string? Notas)
    : ICommand<Guid>;

public class CrearVisitaCommandValidator : AbstractValidator<CrearVisitaCommand>
{
    public CrearVisitaCommandValidator()
    {
        RuleFor(c => c.CentroId).NotEmpty().WithMessage("La visita debe tener un centro.");

        RuleFor(c => c.FechaFin)
            .GreaterThanOrEqualTo(c => c.FechaInicio)
            .WithMessage("La fecha de fin no puede ser anterior a la fecha de inicio.");

        RuleFor(c => c.TrabajadorIds)
            .NotEmpty().WithMessage("La visita debe incluir al menos un trabajador.");

        RuleFor(c => c.Notas)
            .MaximumLength(Visita.LongitudMaximaNotas)
            .WithMessage($"Las notas no pueden superar {Visita.LongitudMaximaNotas} caracteres.");
    }
}

public class CrearVisitaCommandHandler(
    IVisitaRepository repositorio, IVisitaTrabajadorRepository visitaTrabajadorRepositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearVisitaCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearVisitaCommand request, CancellationToken cancellationToken)
    {
        var visita = new Visita(request.CentroId, request.FechaInicio, request.FechaFin, request.Notas);
        repositorio.Agregar(visita);

        foreach (var trabajadorId in request.TrabajadorIds.Distinct())
            visitaTrabajadorRepositorio.Agregar(new VisitaTrabajador(visita.Id, trabajadorId));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(visita.Id);
    }
}
