using CaeManager.Application.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.DocumentosIa;
using CaeManager.Application.Evaluaciones;
using CaeManager.Application.Facturacion;
using CaeManager.Application.Incidencias;
using CaeManager.Application.Facturacion.Queries.ObtenerResumenFacturacion;
using CaeManager.Domain.Incidencias;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Dashboard.Queries;

public record ObtenerCatalogoKpisQuery : IRequest<CatalogoKpisValoresDto>;

public record CentroRiesgoEvaluacionDto(Guid CentroId, string CentroNombre, double PuntuacionMedia);

public record GravedadIncidenciaConteoDto(GravedadIncidencia Gravedad, int Cantidad);

/// <summary>
/// Valores del catálogo completo de KPIs para el tenant activo. Expone
/// también los denominadores de cada media/tasa (<c>Total*</c>) porque el
/// fan-out multi-tenant de <see cref="ObtenerDashboardEjecutivoQuery"/>
/// necesita ponderar por volumen al fusionar varios tenants — un promedio
/// simple entre tenants con volúmenes muy distintos daría un resultado
/// engañoso.
/// </summary>
public record CatalogoKpisValoresDto(
    KpisDashboardDto Documental,
    int TotalDocumentosConVigencia,
    double? PuntuacionMediaEvaluaciones,
    int TotalEvaluaciones,
    IReadOnlyList<CentroRiesgoEvaluacionDto> CentrosConMasRiesgo,
    int IncidenciasAbiertas,
    IReadOnlyList<GravedadIncidenciaConteoDto> IncidenciasPorGravedad,
    double? TiempoMedioResolucionIncidenciasDias,
    int TotalIncidenciasResueltas,
    double? ConfianzaMediaIa,
    decimal CosteIaMesActual,
    double? TiempoMedioProcesamientoIaMs,
    int TotalAuditoriasIaMes,
    decimal FacturacionEstimadaMesActual);

/// <summary>
/// Calcula, para el tenant activo (fijado por <see cref="AmbitoTenantExplicito"/>
/// cuando se llama desde el fan-out multi-tenant, o el tenant de sesión en
/// una carga de un solo tenant), todos los valores del catálogo v1 de
/// KPIs — es la "unidad" que <see cref="ObtenerDashboardEjecutivoQuery"/>
/// llama una vez por tenant.
///
/// Calcular las 5 sub-áreas siempre (aunque el usuario solo haya
/// seleccionado 2 KPIs) es más caro que <see cref="ObtenerKpisDashboardQuery"/>;
/// aceptado para v1 (YAGNI) — si el rendimiento lo exige, una fase futura
/// puede recibir la lista de códigos seleccionados y calcular solo esas
/// sub-áreas.
/// </summary>
public class ObtenerCatalogoKpisQueryHandler(ICentrosQueryContext centrosContext, IDocumentosIaQueryContext documentosIaContext, IEvaluacionesQueryContext evaluacionesContext, IFacturacionQueryContext facturacionContext, IIncidenciasQueryContext incidenciasContext, IAlcanceDatosService alcanceDatos, IMediator mediator)
    : IRequestHandler<ObtenerCatalogoKpisQuery, CatalogoKpisValoresDto>
{
    public async Task<CatalogoKpisValoresDto> Handle(ObtenerCatalogoKpisQuery request, CancellationToken cancellationToken)
    {
        var documental = await mediator.Send(new ObtenerKpisDashboardQuery(), cancellationToken);
        var totalConVigencia = documental.DocumentosVigentes + documental.DocumentosProximos
            + documental.DocumentosUrgentes + documental.DocumentosVencidos;

        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);

        var (puntuacionMedia, totalEvaluaciones, centrosConMasRiesgo) =
            await CalcularEvaluacionesAsync(centroIdsVisibles, cancellationToken);

        var (incidenciasAbiertas, incidenciasPorGravedad, tiempoMedioResolucionDias, totalIncidenciasResueltas) =
            await CalcularIncidenciasAsync(centroIdsVisibles, cancellationToken);

        var (confianzaMedia, costeMes, tiempoMedioMs, totalAuditoriasMes) = await CalcularIaAsync(cancellationToken);

        var facturacionEstimada = await CalcularFacturacionEstimadaAsync(cancellationToken);

        return new CatalogoKpisValoresDto(
            Documental: documental,
            TotalDocumentosConVigencia: totalConVigencia,
            PuntuacionMediaEvaluaciones: puntuacionMedia,
            TotalEvaluaciones: totalEvaluaciones,
            CentrosConMasRiesgo: centrosConMasRiesgo,
            IncidenciasAbiertas: incidenciasAbiertas,
            IncidenciasPorGravedad: incidenciasPorGravedad,
            TiempoMedioResolucionIncidenciasDias: tiempoMedioResolucionDias,
            TotalIncidenciasResueltas: totalIncidenciasResueltas,
            ConfianzaMediaIa: confianzaMedia,
            CosteIaMesActual: costeMes,
            TiempoMedioProcesamientoIaMs: tiempoMedioMs,
            TotalAuditoriasIaMes: totalAuditoriasMes,
            FacturacionEstimadaMesActual: facturacionEstimada);
    }

    private async Task<(double? PuntuacionMedia, int Total, IReadOnlyList<CentroRiesgoEvaluacionDto> CentrosConMasRiesgo)>
        CalcularEvaluacionesAsync(IReadOnlyList<Guid>? centroIdsVisibles, CancellationToken cancellationToken)
    {
        var evaluacionesQuery = evaluacionesContext.Evaluaciones.AsQueryable();
        if (centroIdsVisibles is not null)
            evaluacionesQuery = evaluacionesQuery.Where(e => centroIdsVisibles.Contains(e.CentroId));

        var total = await evaluacionesQuery.CountAsync(cancellationToken);
        if (total == 0) return (null, 0, []);

        var puntuacionMedia = await evaluacionesQuery.AverageAsync(e => (double)e.Puntuacion, cancellationToken);

        var centrosConMasRiesgo = await evaluacionesQuery
            .GroupBy(e => e.CentroId)
            .Select(g => new { CentroId = g.Key, PuntuacionMedia = g.Average(e => (double)e.Puntuacion) })
            .OrderBy(x => x.PuntuacionMedia)
            .Take(5)
            .Join(centrosContext.Centros, x => x.CentroId, c => c.Id,
                (x, c) => new CentroRiesgoEvaluacionDto(c.Id, c.Nombre, x.PuntuacionMedia))
            .ToListAsync(cancellationToken);

        return (puntuacionMedia, total, centrosConMasRiesgo);
    }

    private async Task<(int Abiertas, IReadOnlyList<GravedadIncidenciaConteoDto> PorGravedad, double? TiempoMedioResolucionDias, int TotalResueltas)>
        CalcularIncidenciasAsync(IReadOnlyList<Guid>? centroIdsVisibles, CancellationToken cancellationToken)
    {
        var incidenciasQuery = incidenciasContext.Incidencias.AsQueryable();
        if (centroIdsVisibles is not null)
            incidenciasQuery = incidenciasQuery.Where(i => centroIdsVisibles.Contains(i.CentroId));

        var abiertas = await incidenciasQuery.CountAsync(i => !i.Resuelta, cancellationToken);

        var porGravedad = await incidenciasQuery
            .GroupBy(i => i.Gravedad)
            .Select(g => new GravedadIncidenciaConteoDto(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        var resueltas = await incidenciasQuery
            .Where(i => i.Resuelta && i.ResueltaEnUtc != null)
            .Select(i => new { i.CreadoEnUtc, ResueltaEnUtc = i.ResueltaEnUtc!.Value })
            .ToListAsync(cancellationToken);

        double? tiempoMedioResolucionDias = resueltas.Count == 0
            ? null
            : resueltas.Average(r => (r.ResueltaEnUtc - r.CreadoEnUtc).TotalDays);

        return (abiertas, porGravedad, tiempoMedioResolucionDias, resueltas.Count);
    }

    private async Task<(double? ConfianzaMedia, decimal CosteMes, double? TiempoMedioMs, int TotalAuditoriasMes)>
        CalcularIaAsync(CancellationToken cancellationToken)
    {
        var ahora = DateTime.UtcNow;
        var inicioMes = new DateTime(ahora.Year, ahora.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var auditoriasQuery = documentosIaContext.AuditoriasExtraccionIa.Where(a => a.CreadaEnUtc >= inicioMes);

        var totalAuditoriasMes = await auditoriasQuery.CountAsync(cancellationToken);
        var costeMes = await auditoriasQuery.SumAsync(a => (a.CosteEstimado ?? 0) + (a.CosteEstimadoOcr ?? 0), cancellationToken);

        if (totalAuditoriasMes == 0) return (null, costeMes, null, 0);

        var confianzaMedia = await auditoriasQuery.AverageAsync(a => (double)a.ConfianzaGeneral, cancellationToken);
        var tiempoMedioMs = await auditoriasQuery.AverageAsync(a => (double)a.TiempoProcesamientoMs, cancellationToken);

        return (confianzaMedia, costeMes, tiempoMedioMs, totalAuditoriasMes);
    }

    /// <summary>
    /// Suma el resumen de facturación del mes actual solo de los Clientes que
    /// tienen al menos una <c>TarifaCliente</c> configurada — evita llamar a
    /// <see cref="ObtenerResumenFacturacionQuery"/> (varias sub-queries) por
    /// cada Cliente del tenant cuando la inmensa mayoría no tiene facturación
    /// configurada.
    /// </summary>
    private async Task<decimal> CalcularFacturacionEstimadaAsync(CancellationToken cancellationToken)
    {
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);

        var clientesConTarifaQuery = facturacionContext.TarifasCliente.Select(t => t.ClienteId).Distinct();
        if (clienteIdsVisibles is not null)
            clientesConTarifaQuery = clientesConTarifaQuery.Where(id => clienteIdsVisibles.Contains(id));

        var clientesConTarifa = await clientesConTarifaQuery.ToListAsync(cancellationToken);
        if (clientesConTarifa.Count == 0) return 0m;

        var hoy = DateTime.UtcNow;
        var total = 0m;

        foreach (var clienteId in clientesConTarifa)
        {
            var resumen = await mediator.Send(new ObtenerResumenFacturacionQuery(clienteId, hoy.Year, hoy.Month), cancellationToken);
            if (resumen is not null) total += resumen.TotalEstimado;
        }

        return total;
    }
}
