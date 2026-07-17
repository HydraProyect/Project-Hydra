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
    int DocumentosVigentes,
    int VisitasProgramadas);

/// <summary>
/// Los seis KPI del Dashboard (ver DATABASE.md, hoja "Dashboard" del Excel
/// original). El semáforo de cada documento se calcula en memoria con
/// CalculadoraEstadoDocumento — la misma función que usan las tablas de
/// Documentos — para que Dashboard y detalle nunca puedan mostrar
/// resultados distintos. Los 4 contadores de documentos solo cuentan
/// Documentos de Trabajador — los de Cliente/Empresa quedan fuera de estos
/// KPI por ahora (fuera de alcance).
/// </summary>
public class ObtenerKpisDashboardQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerKpisDashboardQuery, KpisDashboardDto>
{
    public async Task<KpisDashboardDto> Handle(ObtenerKpisDashboardQuery request, CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);

        var trabajadoresQuery = dbContext.Trabajadores.AsQueryable();
        if (trabajadorIdsVisibles is not null) trabajadoresQuery = trabajadoresQuery.Where(t => trabajadorIdsVisibles.Contains(t.Id));
        var trabajadoresActivos = await trabajadoresQuery.CountAsync(cancellationToken);

        var centrosQuery = dbContext.Centros.AsQueryable();
        if (centroIdsVisibles is not null) centrosQuery = centrosQuery.Where(c => centroIdsVisibles.Contains(c.Id));
        var centros = await centrosQuery.CountAsync(cancellationToken);

        var hoyParaVisitas = DateOnly.FromDateTime(DateTime.UtcNow);
        var visitasQuery = dbContext.Visitas.Where(v => v.FechaFin >= hoyParaVisitas);
        if (centroIdsVisibles is not null) visitasQuery = visitasQuery.Where(v => centroIdsVisibles.Contains(v.CentroId));
        var visitasProgramadas = await visitasQuery.CountAsync(cancellationToken);

        var parametros = await dbContext.ParametrosSistema.SingleAsync(cancellationToken);

        var documentosQuery = dbContext.Documentos.Where(d => d.TrabajadorId != null);
        if (trabajadorIdsVisibles is not null) documentosQuery = documentosQuery.Where(d => trabajadorIdsVisibles.Contains(d.TrabajadorId!.Value));

        var fechasVencimiento = await documentosQuery
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
            DocumentosVigentes: estados.Count(e => e == EstadoDocumento.Vigente),
            VisitasProgramadas: visitasProgramadas);
    }
}
