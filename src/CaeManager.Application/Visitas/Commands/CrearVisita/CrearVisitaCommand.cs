using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

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
    IVisitaRepository repositorio, IVisitaTrabajadorRepository visitaTrabajadorRepositorio,
    ICentrosQueryContext centrosContext, ITrabajadoresQueryContext trabajadoresContext, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearVisitaCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearVisitaCommand request, CancellationToken cancellationToken)
    {
        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        if (!await centrosContext.Centros.AnyAsync(c => c.Id == request.CentroId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Visita.CentroNoEncontrado", "No encontramos este centro."));

        var trabajadorIds = request.TrabajadorIds.Distinct().ToList();
        var encontrados = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id))
            .Select(t => t.Id)
            .CountAsync(cancellationToken);

        if (encontrados != trabajadorIds.Count)
            return Result.Fallo<Guid>(Error.Crear("Visita.TrabajadorNoEncontrado", "Alguno de los trabajadores seleccionados no existe."));

        var visita = new Visita(request.CentroId, request.FechaInicio, request.FechaFin, request.Notas);
        repositorio.Agregar(visita);

        foreach (var trabajadorId in trabajadorIds)
            visitaTrabajadorRepositorio.Agregar(new VisitaTrabajador(visita.Id, trabajadorId));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(visita.Id);
    }
}
