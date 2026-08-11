using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Visitas.Antelacion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Visitas.Commands.EditarVisita;

public record EditarVisitaCommand(
    Guid Id, DateOnly FechaInicio, DateOnly FechaFin, IReadOnlyList<Guid> TrabajadorIds, string? Notas,
    Guid Version = default, TimeOnly? HoraEstimadaAcceso = null)
    : ICommand;

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
    ITrabajadoresQueryContext trabajadoresContext, IEvaluadorExpedienteVisitaService evaluadorExpediente,
    IUnitOfWork unitOfWork, ILogger<EditarVisitaCommandHandler> logger)
    : IRequestHandler<EditarVisitaCommand, Result>
{
    public async Task<Result> Handle(EditarVisitaCommand request, CancellationToken cancellationToken)
    {
        var visita = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (visita is null)
            return Result.Fallo(Error.Crear("Visita.NoEncontrada", "No encontramos esta visita."));

        if (ConcurrenciaOptimista.Verificar(visita, request.Version, "esta visita") is { } conflicto)
            return Result.Fallo(conflicto);

        visita.Actualizar(request.FechaInicio, request.FechaFin, request.Notas, request.HoraEstimadaAcceso);

        var trabajadoresActuales = await visitaTrabajadorRepositorio.ObtenerPorVisitaAsync(visita.Id, cancellationToken);
        var trabajadorIdsDeseados = request.TrabajadorIds.Distinct().ToHashSet();
        var trabajadorIdsActuales = trabajadoresActuales.Select(vt => vt.TrabajadorId).ToHashSet();

        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        var trabajadorIdsNuevos = trabajadorIdsDeseados.Except(trabajadorIdsActuales).ToList();
        if (await trabajadoresContext.Trabajadores.Where(t => trabajadorIdsNuevos.Contains(t.Id)).CountAsync(cancellationToken) != trabajadorIdsNuevos.Count)
            return Result.Fallo(Error.Crear("Visita.TrabajadorNoEncontrado", "Alguno de los trabajadores seleccionados no existe."));

        foreach (var vt in trabajadoresActuales.Where(vt => !trabajadorIdsDeseados.Contains(vt.TrabajadorId)))
            visitaTrabajadorRepositorio.Eliminar(vt);

        foreach (var trabajadorId in trabajadorIdsNuevos)
            visitaTrabajadorRepositorio.Agregar(new VisitaTrabajador(visita.Id, trabajadorId));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Cambiar la lista de trabajadores o la hora de acceso puede dejar el expediente
        // completo (o cambiar el momento de entrada contra el que se mide).
        try
        {
            await evaluadorExpediente.EvaluarAsync(visita.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo evaluar el expediente documental de la visita {VisitaId}.", visita.Id);
        }

        return Result.Exito();
    }
}
