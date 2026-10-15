using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Visitas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Proyectos.Queries.ObtenerVisitasProyecto;

public record ObtenerVisitasProyectoQuery(Guid ProyectoId) : IRequest<IReadOnlyList<VisitaProyectoDto>>;

public record VisitaProyectoDto(Guid Id, DateOnly FechaInicio, DateOnly FechaFin, int Trabajadores);

/// <summary>
/// Visitas visibles del Centro que intersectan el periodo del Proyecto. Sin fecha final,
/// el periodo está abierto: no se inventa una fecha de cierre. No crea una relación Proyecto–Visita.
/// Usa la autoridad de ObtenerTecnicosProyecto y el corte de Centros del calendario de Visitas.
/// Los query contexts conservan sus filtros de Tenant propietario y baja lógica.
/// </summary>
public class ObtenerVisitasProyectoQueryHandler(
    IProyectosQueryContext proyectosContext, ICentrosQueryContext centrosContext,
    IVisitasQueryContext visitasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerVisitasProyectoQuery, IReadOnlyList<VisitaProyectoDto>>
{
    public async Task<IReadOnlyList<VisitaProyectoDto>> Handle(ObtenerVisitasProyectoQuery request, CancellationToken cancellationToken)
    {
        var proyecto = await proyectosContext.Proyectos.FirstOrDefaultAsync(p => p.Id == request.ProyectoId, cancellationToken);
        if (proyecto is null || !await ProyectoAutorizacion.VisibleAsync(proyecto.ClienteId, alcanceDatos, cancellationToken))
            return [];
        if (!await alcanceDatos.CentroVisibleAsync(proyecto.CentroId, cancellationToken)
            || !await centrosContext.Centros.AnyAsync(c => c.Id == proyecto.CentroId, cancellationToken))
            return [];

        var fin = proyecto.FechaCierreReal ?? proyecto.FechaFinPrevista;
        var visitas = await visitasContext.Visitas
            .Where(v => v.CentroId == proyecto.CentroId && !v.EstaCancelada
                && v.FechaFin >= proyecto.FechaInicio && (fin == null || v.FechaInicio <= fin))
            .OrderBy(v => v.FechaInicio).ThenBy(v => v.FechaFin).ThenBy(v => v.Id)
            .Select(v => new { v.Id, v.FechaInicio, v.FechaFin }).ToListAsync(cancellationToken);
        if (visitas.Count == 0) return [];
        var ids = visitas.Select(v => v.Id).ToList();
        var trabajadores = await visitasContext.VisitasTrabajadores.Where(v => ids.Contains(v.VisitaId))
            .GroupBy(v => v.VisitaId).Select(g => new { Id = g.Key, Total = g.Count() }).ToListAsync(cancellationToken);
        var porVisita = trabajadores.ToDictionary(v => v.Id, v => v.Total);
        return visitas.Select(v => new VisitaProyectoDto(v.Id, v.FechaInicio, v.FechaFin, porVisita.GetValueOrDefault(v.Id))).ToList();
    }
}
