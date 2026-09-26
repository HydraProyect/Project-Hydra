using CaeManager.Application.Common;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.AsistenteIa.Tareas.Commands;

/// <summary>
/// Guarda el plan entero, o lo sustituye mientras no esté confirmado. Los
/// pasos a los que les falta un dato quedan en borrador dentro de la tarea.
/// <paramref name="AsistidoPorIa"/> marca que el plan salió de una
/// interpretación por IA.
/// </summary>
public record GuardarPlanTareaAsistenteCommand(
    Guid TareaId,
    IReadOnlyList<PasoPlanTareaAsistenteDto> Pasos,
    bool AsistidoPorIa) : ICommand;

public class GuardarPlanTareaAsistenteCommandValidator : AbstractValidator<GuardarPlanTareaAsistenteCommand>
{
    public GuardarPlanTareaAsistenteCommandValidator()
    {
        RuleFor(c => c.TareaId).NotEmpty();
        RuleFor(c => c.Pasos).NotEmpty().WithMessage("Un plan debe tener al menos un paso.");
        RuleFor(c => c.Pasos.Count).LessThanOrEqualTo(TareaAsistente.MaximoPasos).When(c => c.Pasos is not null);
        RuleForEach(c => c.Pasos).SetValidator(new PasoPlanTareaAsistenteDtoValidator());
    }
}

public class GuardarPlanTareaAsistenteCommandHandler(ModificacionTareaAsistente modificacion)
    : IRequestHandler<GuardarPlanTareaAsistenteCommand, Result>
{
    public Task<Result> Handle(GuardarPlanTareaAsistenteCommand request, CancellationToken cancellationToken) =>
        modificacion.AplicarAsync(request.TareaId, versionEsperada: null, (tarea, _) =>
            tarea.GuardarPlan(request.Pasos.Select(p => p.ADefinicion()).ToList(), request.AsistidoPorIa, DateTime.UtcNow),
            cancellationToken);
}
