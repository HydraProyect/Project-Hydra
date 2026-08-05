using CaeManager.Application.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Visitas;
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
    int VisitasProgramadas,
    int TasaCumplimientoDocumental,
    int VisitasUrgentes = 0);

/// <summary>
/// Los seis KPI del Dashboard (ver DATABASE.md, hoja "Dashboard" del Excel
/// original). El semáforo de cada documento se calcula en memoria con
/// CalculadoraEstadoDocumento — la misma función que usan las tablas de
/// Documentos — para que Dashboard y detalle nunca puedan mostrar
/// resultados distintos. Los 4 contadores de documentos solo cuentan
/// Documentos de Trabajador — los de Cliente/Empresa quedan fuera de estos
/// KPI por ahora (fuera de alcance).
/// </summary>
public class ObtenerKpisDashboardQueryHandler(ICentrosQueryContext centrosContext, IConfiguracionQueryContext configuracionContext, IDocumentosQueryContext documentosContext, ITrabajadoresQueryContext trabajadoresContext, IVisitasQueryContext visitasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerKpisDashboardQuery, KpisDashboardDto>
{
    public async Task<KpisDashboardDto> Handle(ObtenerKpisDashboardQuery request, CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);

        var trabajadoresQuery = trabajadoresContext.Trabajadores.AsQueryable();
        if (trabajadorIdsVisibles is not null) trabajadoresQuery = trabajadoresQuery.Where(t => trabajadorIdsVisibles.Contains(t.Id));
        var trabajadoresActivos = await trabajadoresQuery.CountAsync(cancellationToken);

        var centrosQuery = centrosContext.Centros.AsQueryable();
        if (centroIdsVisibles is not null) centrosQuery = centrosQuery.Where(c => centroIdsVisibles.Contains(c.Id));
        var centros = await centrosQuery.CountAsync(cancellationToken);

        var hoyParaVisitas = DateOnly.FromDateTime(DateTime.UtcNow);
        var visitasQuery = visitasContext.Visitas.Where(v => v.FechaFin >= hoyParaVisitas);
        if (centroIdsVisibles is not null) visitasQuery = visitasQuery.Where(v => centroIdsVisibles.Contains(v.CentroId));
        var visitasProgramadas = await visitasQuery.CountAsync(cancellationToken);

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);

        // Fase F: mismo criterio SQL que ObtenerVisitasQuery(SoloUrgentes=true) — ver el comentario de CalculadoraUrgenciaVisita.
        var limiteAvisoVisita = hoyParaVisitas.AddDays(parametros.HorasAvisoVisita / 24);
        var visitasUrgentes = await visitasQuery.CountAsync(v => v.FechaInicio <= limiteAvisoVisita, cancellationToken);

        var documentosQuery = documentosContext.Documentos.Where(d => d.TrabajadorId != null);
        if (trabajadorIdsVisibles is not null) documentosQuery = documentosQuery.Where(d => trabajadorIdsVisibles.Contains(d.TrabajadorId!.Value));

        var fechasVencimiento = await documentosQuery
            .Select(d => d.FechaVencimiento)
            .ToListAsync(cancellationToken);

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var estados = fechasVencimiento
            .Select(f => CalculadoraEstadoDocumento.Calcular(f, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias))
            .ToList();

        var vigentes = estados.Count(e => e == EstadoDocumento.Vigente);
        var proximos = estados.Count(e => e == EstadoDocumento.Proximo);
        var urgentes = estados.Count(e => e == EstadoDocumento.Urgente);
        var vencidos = estados.Count(e => e == EstadoDocumento.Vencido);
        var totalConVigencia = vigentes + proximos + urgentes + vencidos;
        var tasa = totalConVigencia == 0 ? 100 : vigentes * 100 / totalConVigencia;

        return new KpisDashboardDto(
            TrabajadoresActivos: trabajadoresActivos,
            Centros: centros,
            DocumentosVencidos: vencidos,
            DocumentosUrgentes: urgentes,
            DocumentosProximos: proximos,
            DocumentosVigentes: vigentes,
            VisitasProgramadas: visitasProgramadas,
            TasaCumplimientoDocumental: tasa,
            VisitasUrgentes: visitasUrgentes);
    }
}
