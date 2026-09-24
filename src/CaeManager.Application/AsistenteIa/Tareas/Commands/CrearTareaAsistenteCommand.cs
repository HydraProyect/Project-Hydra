using CaeManager.Application.Common;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.AsistenteIa.Tareas.Commands;

/// <summary>
/// Abre una tarea del asistente con la orden que escribió la persona.
/// <paramref name="TextoOriginal"/> es ese texto tal cual —la evidencia de la
/// solicitud—; <paramref name="TextoEnmascarado"/>, la versión con marcadores
/// que se envió al proveedor de IA, si se envió.
/// </summary>
public record CrearTareaAsistenteCommand(string TextoOriginal, string? TextoEnmascarado = null) : ICommand<Guid>;

public class CrearTareaAsistenteCommandValidator : AbstractValidator<CrearTareaAsistenteCommand>
{
    public CrearTareaAsistenteCommandValidator()
    {
        RuleFor(c => c.TextoOriginal).NotEmpty().WithMessage("Escribe qué quieres hacer.");
        RuleFor(c => c.TextoOriginal).MaximumLength(TurnoTareaAsistente.LongitudMaximaTexto);
        RuleFor(c => c.TextoEnmascarado).MaximumLength(TurnoTareaAsistente.LongitudMaximaTexto);
    }
}

/// <summary>
/// Quién puede escribir lo decide <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>
/// (roles de escritura; ninguna Sesión Privilegiada). Este handler añade las
/// comprobaciones de <see cref="ResolucionPersonaTareaAsistente"/>. El Tenant
/// propietario lo sella TenantSelladoInterceptor con el Tenant en el que se
/// trabaja.
/// </summary>
public class CrearTareaAsistenteCommandHandler(
    ITareaAsistenteRepository repositorio,
    IActorAuditoria actorAuditoria,
    ICurrentUserService currentUserService,
    ITenantActual tenantActual,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CrearTareaAsistenteCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearTareaAsistenteCommand request, CancellationToken cancellationToken)
    {
        var persona = await ResolucionPersonaTareaAsistente.ResolverAsync(actorAuditoria, currentUserService, tenantActual);
        if (persona.EsFallido)
            return Result.Fallo<Guid>(persona.Error);

        var ahora = DateTime.UtcNow;
        var p = persona.Valor;
        var tarea = new TareaAsistente(p.ActorRealUsuarioId, p.UsuarioSimuladoId, p.TenantOrigenId, p.Via, p.ViaAccesoId, ahora);
        tarea.AgregarTurnoDePersona(request.TextoOriginal, request.TextoEnmascarado, ahora);

        repositorio.Agregar(tarea);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito(tarea.Id);
    }
}
