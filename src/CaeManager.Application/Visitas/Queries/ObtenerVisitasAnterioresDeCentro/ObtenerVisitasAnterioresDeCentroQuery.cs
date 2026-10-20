using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Visitas.Queries.ObtenerVisitasAnterioresDeCentro;

/// <summary>Lectura de Centro 360: visitas pasadas y canceladas, sin alterar la ventana de riesgo de las visitas activas.</summary>
public record ObtenerVisitasAnterioresDeCentroQuery(Guid CentroId) : IRequest<IReadOnlyList<VisitaAnteriorDeCentroDto>>;

public record VisitaAnteriorDeCentroDto(Guid Id, DateOnly FechaInicio, DateOnly FechaFin,
    bool EstaCancelada, bool DocumentacionGestionada, IReadOnlyList<string> Trabajadores);

public class ObtenerVisitasAnterioresDeCentroQueryHandler(
    IVisitasQueryContext visitasContext, ITrabajadoresQueryContext trabajadoresContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerVisitasAnterioresDeCentroQuery, IReadOnlyList<VisitaAnteriorDeCentroDto>>
{
    public async Task<IReadOnlyList<VisitaAnteriorDeCentroDto>> Handle(
        ObtenerVisitasAnterioresDeCentroQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.CentroVisibleAsync(request.CentroId, cancellationToken))
            return [];

        var hoy = DiaDeNegocio.Hoy();
        var visitas = await visitasContext.Visitas
            .Where(v => v.CentroId == request.CentroId && (v.FechaFin < hoy || v.EstaCancelada))
            .OrderByDescending(v => v.FechaInicio).ThenByDescending(v => v.FechaFin).ThenBy(v => v.Id)
            .Select(v => new { v.Id, v.FechaInicio, v.FechaFin, v.EstaCancelada, v.DocumentacionGestionadaEnUtc })
            .ToListAsync(cancellationToken);
        if (visitas.Count == 0)
            return [];

        var visitaIds = visitas.Select(v => v.Id).ToList();
        var trabajadoresVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var trabajadores = trabajadoresContext.Trabajadores;
        if (trabajadoresVisibles is not null)
            trabajadores = trabajadores.Where(t => trabajadoresVisibles.Contains(t.Id));

        var participantes = await (
            from vt in visitasContext.VisitasTrabajadores
            join t in trabajadores on vt.TrabajadorId equals t.Id
            where visitaIds.Contains(vt.VisitaId)
            orderby t.Apellidos, t.Nombre, t.Id
            select new { vt.VisitaId, t.Nombre, t.Apellidos }).ToListAsync(cancellationToken);

        // Participación prevista no acredita asistencia. Tampoco se deduce quién gestionó la Visita.
        return visitas.Select(v => new VisitaAnteriorDeCentroDto(v.Id, v.FechaInicio, v.FechaFin,
            v.EstaCancelada, v.DocumentacionGestionadaEnUtc is not null,
            participantes.Where(t => t.VisitaId == v.Id).Select(t => $"{t.Nombre} {t.Apellidos}").ToList())).ToList();
    }
}
