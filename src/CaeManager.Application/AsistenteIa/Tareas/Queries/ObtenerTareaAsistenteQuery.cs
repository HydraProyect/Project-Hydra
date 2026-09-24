using CaeManager.Application.Common;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.AsistenteIa.Tareas.Queries;

/// <summary>La conversación y el plan de una tarea del asistente de la persona, para reanudarla.</summary>
public record ObtenerTareaAsistenteQuery(Guid TareaId) : IRequest<Result<TareaAsistenteDto>>;

public record TareaAsistenteDto(
    Guid Id,
    Guid Version,
    EstadoTareaAsistente Estado,
    DateTime CreadaEnUtc,
    DateTime ActualizadaEnUtc,
    DateTime? PlanConfirmadoEnUtc,
    IReadOnlyList<TurnoTareaAsistenteDto> Turnos,
    IReadOnlyList<PasoTareaAsistenteDto> Pasos);

/// <summary>
/// Solo <see cref="TextoOriginal"/>, en claro: la versión enmascarada es un
/// artefacto del envío al proveedor y la persona no la necesita para retomar
/// la conversación.
/// </summary>
public record TurnoTareaAsistenteDto(int Numero, AutorTurnoTareaAsistente Autor, string TextoOriginal, DateTime FechaUtc);

public record PasoTareaAsistenteDto(
    Guid Id,
    int Posicion,
    string OrdenAsistenteId,
    string DatosJson,
    string? Resumen,
    IReadOnlyList<string> CamposPendientes,
    IReadOnlyList<AvisoPasoTareaAsistente> Avisos,
    bool AsistidoPorIa,
    EstadoPasoTareaAsistente Estado,
    string? MotivoFallo);

public class ObtenerTareaAsistenteQueryHandler(
    ITareasAsistenteQueryContext contexto,
    IActorAuditoria actorAuditoria,
    ICurrentUserService currentUserService,
    ITenantActual tenantActual)
    : IRequestHandler<ObtenerTareaAsistenteQuery, Result<TareaAsistenteDto>>
{
    public async Task<Result<TareaAsistenteDto>> Handle(ObtenerTareaAsistenteQuery request, CancellationToken cancellationToken)
    {
        var persona = await ResolucionPersonaTareaAsistente.ResolverAsync(actorAuditoria, currentUserService, tenantActual);
        if (persona.EsFallido)
            return Result.Fallo<TareaAsistenteDto>(persona.Error);

        var actorReal = persona.Valor.ActorRealUsuarioId;
        var tarea = await contexto.TareasAsistente
            .AsNoTracking()
            .Where(t => t.Id == request.TareaId && t.ActorRealUsuarioId == actorReal)
            .Select(t => new { t.Id, t.Version, t.Estado, t.CreadaEnUtc, t.ActualizadaEnUtc, t.PlanConfirmadoEnUtc })
            .FirstOrDefaultAsync(cancellationToken);
        if (tarea is null)
            return Result.Fallo<TareaAsistenteDto>(ResolucionPersonaTareaAsistente.NoEncontrada());

        var turnos = await contexto.TurnosTareaAsistente
            .AsNoTracking()
            .Where(u => u.TareaAsistenteId == tarea.Id)
            .OrderBy(u => u.Numero)
            .Select(u => new TurnoTareaAsistenteDto(u.Numero, u.Autor, u.TextoOriginal, u.FechaUtc))
            .ToListAsync(cancellationToken);

        var pasos = await contexto.PasosTareaAsistente
            .AsNoTracking()
            .Where(p => p.TareaAsistenteId == tarea.Id)
            .OrderBy(p => p.Posicion)
            .ToListAsync(cancellationToken);

        return Result.Exito(new TareaAsistenteDto(
            tarea.Id,
            tarea.Version,
            tarea.Estado,
            tarea.CreadaEnUtc,
            tarea.ActualizadaEnUtc,
            tarea.PlanConfirmadoEnUtc,
            turnos,
            pasos.Select(p => new PasoTareaAsistenteDto(
                p.Id, p.Posicion, p.OrdenAsistenteId, p.DatosJson, p.Resumen, p.CamposPendientes, p.Avisos,
                p.AsistidoPorIa, p.Estado, p.MotivoFallo)).ToList()));
    }
}
