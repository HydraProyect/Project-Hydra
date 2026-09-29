using CaeManager.Application.AsistenteIa.Queries.ProponerPlan;
using CaeManager.Application.AsistenteIa.Tareas.Queries;
using CaeManager.Application.Common;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.AsistenteIa.Tareas.Commands;

/// <summary>
/// Pide el plan de una Tarea del asistente al proveedor de IA: la primera vez y cada
/// vez que la persona cambia el Tenant destino. Es un <b>Command</b> a propósito, aunque
/// solo lea: así la autorización de escritura (<see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>)
/// se revalida <b>antes de cada envío del texto al proveedor</b>, no solo al guardar el
/// plan. Una persona cuya Asignación de Cartera pierde el permiso de escritura con el
/// circuito abierto no puede volver a mandar su texto fuera.
/// <para>
/// El texto no lo trae el llamador: sale del primer turno de la propia Tarea, que ya
/// comprobó que es de esta persona (<see cref="ObtenerTareaAsistenteQuery"/>). Solo se
/// propone sobre una Tarea cuyo plan aún se puede modificar.
/// </para>
/// </summary>
public record ProponerPlanDeTareaAsistenteCommand(Guid TareaId, Guid? TenantElegido = null) : ICommand<PlanPropuestoDto>;

public class ProponerPlanDeTareaAsistenteCommandValidator : AbstractValidator<ProponerPlanDeTareaAsistenteCommand>
{
    public ProponerPlanDeTareaAsistenteCommandValidator() => RuleFor(c => c.TareaId).NotEmpty();
}

public class ProponerPlanDeTareaAsistenteCommandHandler(IMediator mediator)
    : IRequestHandler<ProponerPlanDeTareaAsistenteCommand, Result<PlanPropuestoDto>>
{
    public async Task<Result<PlanPropuestoDto>> Handle(
        ProponerPlanDeTareaAsistenteCommand request, CancellationToken cancellationToken)
    {
        var tarea = await mediator.Send(new ObtenerTareaAsistenteQuery(request.TareaId), cancellationToken);
        if (tarea.EsFallido)
            return Result.Fallo<PlanPropuestoDto>(tarea.Error);

        if (tarea.Valor.Estado is not (EstadoTareaAsistente.Conversando
            or EstadoTareaAsistente.PlanEnBorrador or EstadoTareaAsistente.PlanListo))
            return Result.Fallo<PlanPropuestoDto>(Error.Crear(
                "AsistenteIa.TareaNoModificable", "Esta tarea del asistente ya no admite un plan nuevo."));

        var texto = tarea.Valor.Turnos
            .Where(t => t.Autor == AutorTurnoTareaAsistente.Persona)
            .OrderBy(t => t.Numero)
            .Select(t => t.TextoOriginal)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(texto))
            return Result.Fallo<PlanPropuestoDto>(Error.Crear(
                "AsistenteIa.TareaSinTexto", "La tarea del asistente no tiene la orden que se escribió."));

        return await mediator.Send(new ProponerPlanAsistenteQuery(texto, request.TenantElegido), cancellationToken);
    }
}
