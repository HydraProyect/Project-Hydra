using CaeManager.Application.Common;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.AsistenteIa.Tareas.Queries;

/// <summary>
/// Las tareas del asistente de la persona en el Tenant en el que trabaja, las
/// más recientes primero. Con <paramref name="SoloAbiertas"/>, solo las que se
/// pueden retomar (ni terminadas ni descartadas).
/// </summary>
public record ListarTareasAsistenteQuery(bool SoloAbiertas = true, int Maximo = 50) : IRequest<Result<IReadOnlyList<ResumenTareaAsistenteDto>>>;

public record ResumenTareaAsistenteDto(
    Guid Id,
    EstadoTareaAsistente Estado,
    string PrimerTextoOriginal,
    DateTime ActualizadaEnUtc);

public class ListarTareasAsistenteQueryHandler(
    ITareasAsistenteQueryContext contexto,
    IActorAuditoria actorAuditoria,
    ICurrentUserService currentUserService,
    ITenantActual tenantActual)
    : IRequestHandler<ListarTareasAsistenteQuery, Result<IReadOnlyList<ResumenTareaAsistenteDto>>>
{
    public const int MaximoPermitido = 200;

    public async Task<Result<IReadOnlyList<ResumenTareaAsistenteDto>>> Handle(
        ListarTareasAsistenteQuery request, CancellationToken cancellationToken)
    {
        var persona = await ResolucionPersonaTareaAsistente.ResolverAsync(actorAuditoria, currentUserService, tenantActual);
        if (persona.EsFallido)
            return Result.Fallo<IReadOnlyList<ResumenTareaAsistenteDto>>(persona.Error);

        var actorReal = persona.Valor.ActorRealUsuarioId;
        var consulta = contexto.TareasAsistente.AsNoTracking().Where(t => t.ActorRealUsuarioId == actorReal);
        if (request.SoloAbiertas)
            consulta = consulta.Where(t => t.Estado != EstadoTareaAsistente.Terminada && t.Estado != EstadoTareaAsistente.Descartada);

        var maximo = Math.Clamp(request.Maximo, 1, MaximoPermitido);
        IReadOnlyList<ResumenTareaAsistenteDto> tareas = await consulta
            .OrderByDescending(t => t.ActualizadaEnUtc)
            .Take(maximo)
            .Select(t => new ResumenTareaAsistenteDto(
                t.Id,
                t.Estado,
                contexto.TurnosTareaAsistente
                    .Where(u => u.TareaAsistenteId == t.Id && u.Numero == 1)
                    .Select(u => u.TextoOriginal)
                    .FirstOrDefault() ?? string.Empty,
                t.ActualizadaEnUtc))
            .ToListAsync(cancellationToken);

        return Result.Exito(tareas);
    }
}
