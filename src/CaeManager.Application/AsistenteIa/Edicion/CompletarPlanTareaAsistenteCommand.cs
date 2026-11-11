using CaeManager.Application.AsistenteIa.Queries.ProponerPlan;
using CaeManager.Application.AsistenteIa.Tareas;
using CaeManager.Application.AsistenteIa.Tareas.Queries;
using CaeManager.Application.Common;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.AsistenteIa.Edicion;

/// <summary>
/// El Gestor CAE completa o corrige a mano los datos del plan de una Tarea del asistente
/// (la persona que se da de alta, las fechas, el Centro…). No llama al proveedor de IA:
/// revisa los valores en el Tenant efectivo con <see cref="RevisionPlanAsistente"/>
/// —fallo cerrado ante cualquier valor que no sea de la cartera— y guarda el plan
/// resultante, con un paso por operación (<see cref="PlanPropuestoATareaAsistente.APasos"/>).
/// <para>
/// La Tarea tiene que ser de la persona (<see cref="ModificacionTareaAsistente"/>) y la
/// versión, la que vio (<paramref name="VersionVista"/>): dos pestañas no se pisan.
/// </para>
/// </summary>
public record CompletarPlanTareaAsistenteCommand(
    Guid TareaId,
    Guid VersionVista,
    int ConfianzaOrden,
    IReadOnlyList<ValorDatoPlanDto> Valores) : ICommand<PlanCompletadoDto>;

public record PlanCompletadoDto(PlanPropuestoDto Plan, Guid Version);

public class CompletarPlanTareaAsistenteCommandValidator : AbstractValidator<CompletarPlanTareaAsistenteCommand>
{
    public CompletarPlanTareaAsistenteCommandValidator()
    {
        RuleFor(c => c.TareaId).NotEmpty();
        RuleFor(c => c.Valores).NotNull();
        RuleFor(c => c.Valores.Count).LessThanOrEqualTo(32).When(c => c.Valores is not null);
        RuleFor(c => c.ConfianzaOrden).InclusiveBetween(0, 100);
    }
}

public class CompletarPlanTareaAsistenteCommandHandler(
    IMediator mediator, RevisionPlanAsistente revision, ModificacionTareaAsistente modificacion)
    : IRequestHandler<CompletarPlanTareaAsistenteCommand, Result<PlanCompletadoDto>>
{
    public async Task<Result<PlanCompletadoDto>> Handle(
        CompletarPlanTareaAsistenteCommand request, CancellationToken cancellationToken)
    {
        var tarea = await mediator.Send(new ObtenerTareaAsistenteQuery(request.TareaId), cancellationToken);
        if (tarea.EsFallido)
            return Result.Fallo<PlanCompletadoDto>(tarea.Error);

        if (tarea.Valor.Estado is not (EstadoTareaAsistente.PlanEnBorrador or EstadoTareaAsistente.PlanListo)
            || tarea.Valor.Pasos.FirstOrDefault() is not { } primero)
            return Result.Fallo<PlanCompletadoDto>(Error.Crear(
                "AsistenteIa.TareaNoModificable", "Este plan ya no se puede cambiar."));

        var revisado = await revision.RevisarAsync(
            primero.OrdenAsistenteId, request.ConfianzaOrden, request.Valores, cancellationToken);
        if (revisado.EsFallido)
            return Result.Fallo<PlanCompletadoDto>(revisado.Error);

        var plan = revisado.Valor.Plan;
        var pasos = PlanPropuestoATareaAsistente.APasos(plan);
        var guardado = await modificacion.AplicarAsync(request.TareaId, request.VersionVista, (t, _) =>
            t.GuardarPlan(pasos.Select(p => p.ADefinicion()).ToList(), asistidoPorIa: true, DateTime.UtcNow),
            cancellationToken);
        if (guardado.EsFallido)
            return Result.Fallo<PlanCompletadoDto>(guardado.Error);

        var releida = await mediator.Send(new ObtenerTareaAsistenteQuery(request.TareaId), cancellationToken);
        if (releida.EsFallido)
            return Result.Fallo<PlanCompletadoDto>(releida.Error);

        return new PlanCompletadoDto(plan, releida.Valor.Version);
    }
}
