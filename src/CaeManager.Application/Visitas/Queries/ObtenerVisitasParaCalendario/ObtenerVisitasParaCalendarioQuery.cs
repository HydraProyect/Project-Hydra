using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Visitas.Queries.ObtenerVisitasParaCalendario;

/// <summary>
/// Visitas cuyo rango [FechaInicio, FechaFin] intersecta el mes — a
/// diferencia de ObtenerVencimientosMesQuery (un vencimiento cae en un solo
/// día), una Visita puede empezar antes del mes visible o terminar después,
/// así que el filtro es de solapamiento de rangos, no de fecha exacta.
/// </summary>
public record ObtenerVisitasParaCalendarioQuery(int Anio, int Mes) : IRequest<IReadOnlyList<VisitaCalendarioDto>>;

public record VisitaCalendarioDto(
    Guid Id,
    string CentroNombre,
    string ClienteRazonSocial,
    DateOnly FechaInicio,
    DateOnly FechaFin,
    int TotalTrabajadores,
    bool NotificadoCliente);

public class ObtenerVisitasParaCalendarioQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerVisitasParaCalendarioQuery, IReadOnlyList<VisitaCalendarioDto>>
{
    public async Task<IReadOnlyList<VisitaCalendarioDto>> Handle(ObtenerVisitasParaCalendarioQuery request, CancellationToken cancellationToken)
    {
        var primerDia = new DateOnly(request.Anio, request.Mes, 1);
        var ultimoDia = primerDia.AddMonths(1).AddDays(-1);
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);

        var visitas = await (
            from visita in dbContext.Visitas
            join centro in dbContext.Centros on visita.CentroId equals centro.Id
            join cliente in dbContext.Clientes on centro.ClienteId equals cliente.Id
            where visita.FechaInicio <= ultimoDia && visita.FechaFin >= primerDia
            where centroIdsVisibles == null || centroIdsVisibles.Contains(centro.Id)
            select new
            {
                visita.Id,
                CentroNombre = centro.Nombre,
                ClienteRazonSocial = cliente.RazonSocial,
                visita.FechaInicio,
                visita.FechaFin,
                visita.NotificadoCliente
            })
            .ToListAsync(cancellationToken);

        if (visitas.Count == 0) return [];

        var visitaIds = visitas.Select(v => v.Id).ToList();
        var conteoTrabajadores = await dbContext.VisitasTrabajadores
            .Where(vt => visitaIds.Contains(vt.VisitaId))
            .GroupBy(vt => vt.VisitaId)
            .Select(g => new { VisitaId = g.Key, Total = g.Count() })
            .ToListAsync(cancellationToken);

        return visitas
            .Select(v => new VisitaCalendarioDto(
                v.Id, v.CentroNombre, v.ClienteRazonSocial, v.FechaInicio, v.FechaFin,
                conteoTrabajadores.FirstOrDefault(c => c.VisitaId == v.Id)?.Total ?? 0,
                v.NotificadoCliente))
            .OrderBy(v => v.FechaInicio)
            .ToList();
    }
}
