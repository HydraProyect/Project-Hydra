using CaeManager.Application.Common;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.AsistenteIa.Tareas.Commands;

/// <summary>
/// La persona confirma el plan entero (el Enter). <paramref name="VersionVista"/>
/// es la <see cref="TareaAsistente.Version"/> del plan que tenía delante: si la
/// tarea cambió después —un paso actualizado, un plan sustituido, otra
/// pestaña—, no se confirma algo que no vio.
/// </summary>
public record ConfirmarPlanTareaAsistenteCommand(Guid TareaId, Guid VersionVista) : ICommand;

public class ConfirmarPlanTareaAsistenteCommandValidator : AbstractValidator<ConfirmarPlanTareaAsistenteCommand>
{
    public ConfirmarPlanTareaAsistenteCommandValidator()
    {
        RuleFor(c => c.TareaId).NotEmpty();
        // Obligatoria: ConcurrenciaOptimista.Verificar deja pasar Guid.Empty.
        RuleFor(c => c.VersionVista).NotEmpty().WithMessage("Falta la versión del plan que se confirma.");
    }
}

/// <summary>
/// Quien confirma queda registrado como Actor real, separado del Usuario
/// simulado. El dominio exige que sea la persona propietaria de la tarea.
/// </summary>
public class ConfirmarPlanTareaAsistenteCommandHandler(ModificacionTareaAsistente modificacion)
    : IRequestHandler<ConfirmarPlanTareaAsistenteCommand, Result>
{
    public Task<Result> Handle(ConfirmarPlanTareaAsistenteCommand request, CancellationToken cancellationToken) =>
        modificacion.AplicarAsync(request.TareaId, request.VersionVista, (tarea, persona) =>
            tarea.ConfirmarPlan(persona.ActorRealUsuarioId, persona.UsuarioSimuladoId, DateTime.UtcNow),
            cancellationToken);
}
