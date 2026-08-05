using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;

/// <summary>
/// Alimenta el badge "Visita dd/mm–dd/mm" del acordeón de Centro 360
/// (PLAN-EJECUCION-UX.md § 0.3) — proyección de Visitas, sin modelo nuevo.
/// Por cada Centro de la lista, la visita activa (<c>FechaFin >= hoy</c>,
/// cubre "en curso" y "próxima") con el <c>FechaInicio</c> más cercano; si
/// hay varias en curso a la vez para el mismo centro, se prioriza la que
/// antes termina (la ventana de riesgo más apremiante).
/// </summary>
public record ObtenerProximaVisitaPorCentroQuery(IReadOnlyList<Guid> CentroIds)
    : IRequest<IReadOnlyDictionary<Guid, VisitaResumenDto>>;

public record VisitaResumenDto(Guid VisitaId, DateOnly FechaInicio, DateOnly FechaFin);

public class ObtenerProximaVisitaPorCentroQueryHandler(IVisitasQueryContext visitasContext)
    : IRequestHandler<ObtenerProximaVisitaPorCentroQuery, IReadOnlyDictionary<Guid, VisitaResumenDto>>
{
    public async Task<IReadOnlyDictionary<Guid, VisitaResumenDto>> Handle(
        ObtenerProximaVisitaPorCentroQuery request, CancellationToken cancellationToken)
    {
        if (request.CentroIds.Count == 0)
            return new Dictionary<Guid, VisitaResumenDto>();

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var visitasActivas = await visitasContext.Visitas
            .Where(v => request.CentroIds.Contains(v.CentroId) && v.FechaFin >= hoy)
            .Select(v => new { v.Id, v.CentroId, v.FechaInicio, v.FechaFin })
            .ToListAsync(cancellationToken);

        return visitasActivas
            .GroupBy(v => v.CentroId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(v => v.FechaFin).ThenBy(v => v.FechaInicio).Select(v => new VisitaResumenDto(v.Id, v.FechaInicio, v.FechaFin)).First());
    }
}
