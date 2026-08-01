using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Visitas.Commands.EditarVisita;

public record EditarVisitaCommand(
    Guid Id, DateOnly FechaInicio, DateOnly FechaFin, IReadOnlyList<Guid> TrabajadorIds, string? Notas,
    Guid Version = default)
    : IRequest<Result>;

public class EditarVisitaCommandValidator : AbstractValidator<EditarVisitaCommand>
{
    public EditarVisitaCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();

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

public class EditarVisitaCommandHandler(
    IVisitaRepository repositorio, IVisitaTrabajadorRepository visitaTrabajadorRepositorio,
    IApplicationDbContext dbContext, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarVisitaCommand, Result>
{
    public async Task<Result> Handle(EditarVisitaCommand request, CancellationToken cancellationToken)
    {
        var visita = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (visita is null)
            return Result.Fallo(Error.Crear("Visita.NoEncontrada", "No encontramos esta visita."));

        if (ConcurrenciaOptimista.Verificar(visita, request.Version, "esta visita") is { } conflicto)
            return Result.Fallo(conflicto);

        visita.Actualizar(request.FechaInicio, request.FechaFin, request.Notas);

        var trabajadoresActuales = await visitaTrabajadorRepositorio.ObtenerPorVisitaAsync(visita.Id, cancellationToken);
        var trabajadorIdsDeseados = request.TrabajadorIds.Distinct().ToHashSet();
        var trabajadorIdsActuales = trabajadoresActuales.Select(vt => vt.TrabajadorId).ToHashSet();

        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        var trabajadorIdsNuevos = trabajadorIdsDeseados.Except(trabajadorIdsActuales).ToList();
        if (await dbContext.Trabajadores.Where(t => trabajadorIdsNuevos.Contains(t.Id)).CountAsync(cancellationToken) != trabajadorIdsNuevos.Count)
            return Result.Fallo(Error.Crear("Visita.TrabajadorNoEncontrado", "Alguno de los trabajadores seleccionados no existe."));

        foreach (var vt in trabajadoresActuales.Where(vt => !trabajadorIdsDeseados.Contains(vt.TrabajadorId)))
            visitaTrabajadorRepositorio.Eliminar(vt);

        foreach (var trabajadorId in trabajadorIdsNuevos)
            visitaTrabajadorRepositorio.Agregar(new VisitaTrabajador(visita.Id, trabajadorId));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
