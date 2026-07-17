using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Dashboard.Queries;

public record ObtenerKpisDashboardQuery : IRequest<KpisDashboardDto>;

public record KpisDashboardDto(
    int TrabajadoresActivos,
    int Centros,
    int DocumentosVencidos,
    int DocumentosUrgentes,
    int DocumentosProximos,
    int DocumentosVigentes);

/// <summary>
/// Los seis KPI del Dashboard (ver DATABASE.md, hoja "Dashboard" del Excel
/// original). El semáforo de cada documento se calcula en memoria con
/// CalculadoraEstadoDocumento — la misma función que usan las tablas de
/// Documentos — para que Dashboard y detalle nunca puedan mostrar
/// resultados distintos.
/// </summary>
public class ObtenerKpisDashboardQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerKpisDashboardQuery, KpisDashboardDto>
{
    public async Task<KpisDashboardDto> Handle(ObtenerKpisDashboardQuery request, CancellationToken cancellationToken)
    {
        var trabajadoresActivos = await dbContext.Trabajadores.CountAsync(cancellationToken);
        var centros = await dbContext.Centros.CountAsync(cancellationToken);

        var parametros = await dbContext.ParametrosSistema.SingleAsync(cancellationToken);

        var fechasVencimiento = await dbContext.Documentos
            .Select(d => d.FechaVencimiento)
            .ToListAsync(cancellationToken);

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var estados = fechasVencimiento
            .Select(f => CalculadoraEstadoDocumento.Calcular(f, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias))
            .ToList();

        return new KpisDashboardDto(
            TrabajadoresActivos: trabajadoresActivos,
            Centros: centros,
            DocumentosVencidos: estados.Count(e => e == EstadoDocumento.Vencido),
            DocumentosUrgentes: estados.Count(e => e == EstadoDocumento.Urgente),
            DocumentosProximos: estados.Count(e => e == EstadoDocumento.Proximo),
            DocumentosVigentes: estados.Count(e => e == EstadoDocumento.Vigente));
    }
}
