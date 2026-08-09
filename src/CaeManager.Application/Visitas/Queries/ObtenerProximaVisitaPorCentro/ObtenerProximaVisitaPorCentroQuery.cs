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
    : IRequest<IReadOnlyDictionary<Guid, IReadOnlyList<VisitaResumenDto>>>;

public record VisitaResumenDto(Guid VisitaId, DateOnly FechaInicio, DateOnly FechaFin);

public class ObtenerProximaVisitaPorCentroQueryHandler(IVisitasQueryContext visitasContext)
    : IRequestHandler<ObtenerProximaVisitaPorCentroQuery, IReadOnlyDictionary<Guid, IReadOnlyList<VisitaResumenDto>>>
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<VisitaResumenDto>>> Handle(
        ObtenerProximaVisitaPorCentroQuery request, CancellationToken cancellationToken)
    {
        if (request.CentroIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<VisitaResumenDto>>();

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var visitasActivas = await visitasContext.Visitas
            .Where(v => request.CentroIds.Contains(v.CentroId) && v.FechaFin >= hoy)
            .Select(v => new { v.Id, v.CentroId, v.FechaInicio, v.FechaFin })
            .ToListAsync(cancellationToken);

        // Se devuelven TODAS las visitas activas, no solo la primera.
        //
        // Las visitas NO se fusionan en un rango (DDL-035, OD-18): dos visitas
        // del 12 al 14 y del 20 al 22 no son "del 12 al 22". Un rango fusionado
        // dice que el centro está visitado once días seguidos, que es falso, y
        // borra la información que el gestor necesita — cuántas visitas hay y
        // cuándo es cada una.
        //
        // El handler ya cargaba todas y descartaba el resto con .First(); solo
        // deja de tirarlas.
        return visitasActivas
            .GroupBy(v => v.CentroId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<VisitaResumenDto>)g
                    .OrderBy(v => v.FechaFin).ThenBy(v => v.FechaInicio)
                    .Select(v => new VisitaResumenDto(v.Id, v.FechaInicio, v.FechaFin))
                    .ToList());
    }
}
