using CaeManager.Application.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.DocumentosIa;
using CaeManager.Application.Facturacion;
using CaeManager.Application.Incidencias;
using CaeManager.Application.Facturacion.Queries.ObtenerResumenFacturacion;
using CaeManager.Domain.Incidencias;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Dashboard.Queries;

public record ObtenerCatalogoKpisQuery : IRequest<CatalogoKpisValoresDto>;

public record CentroCumplimientoDto(Guid CentroId, string CentroNombre, int Porcentaje);

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
    double? PorcentajeCumplimientoDocumental,
    int TotalRequeridosCumplimiento,
    IReadOnlyList<CentroCumplimientoDto> CentrosConMenorCumplimiento,
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
public class ObtenerCatalogoKpisQueryHandler(ICentrosQueryContext centrosContext, IDocumentosIaQueryContext documentosIaContext, ICalculoEstadoCentroService calculoEstadoCentro, IFacturacionQueryContext facturacionContext, IIncidenciasQueryContext incidenciasContext, IAlcanceDatosService alcanceDatos, IMediator mediator)
    : IRequestHandler<ObtenerCatalogoKpisQuery, CatalogoKpisValoresDto>
{
    public async Task<CatalogoKpisValoresDto> Handle(ObtenerCatalogoKpisQuery request, CancellationToken cancellationToken)
    {
        var documental = await mediator.Send(new ObtenerKpisDashboardQuery(), cancellationToken);
        var totalConVigencia = documental.DocumentosVigentes + documental.DocumentosProximos
            + documental.DocumentosUrgentes + documental.DocumentosVencidos;

        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);

        var (porcentajeCumplimiento, totalRequeridos, centrosConMenorCumplimiento) =
            await CalcularCumplimientoAsync(centroIdsVisibles, cancellationToken);

        var (incidenciasAbiertas, incidenciasPorGravedad, tiempoMedioResolucionDias, totalIncidenciasResueltas) =
            await CalcularIncidenciasAsync(centroIdsVisibles, cancellationToken);

        var (confianzaMedia, costeMes, tiempoMedioMs, totalAuditoriasMes) = await CalcularIaAsync(cancellationToken);

        var facturacionEstimada = await CalcularFacturacionEstimadaAsync(cancellationToken);

        return new CatalogoKpisValoresDto(
            Documental: documental,
            TotalDocumentosConVigencia: totalConVigencia,
            PorcentajeCumplimientoDocumental: porcentajeCumplimiento,
            TotalRequeridosCumplimiento: totalRequeridos,
            CentrosConMenorCumplimiento: centrosConMenorCumplimiento,
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

    /// <summary>
    /// Reutiliza <see cref="ICalculoEstadoCentroService.CalcularCumplimientoAsync"/>
    /// (Centro 360, PLAN-EJECUCION-UX.md § 0.5) — sustituye al antiguo KPI de
    /// Evaluaciones (retirado). <c>TotalRequeridosCumplimiento</c> es el
    /// denominador que <see cref="ObtenerDashboardEjecutivoQuery"/> necesita
    /// para ponderar el % al fusionar varios tenants.
    /// </summary>
    private async Task<(double? Porcentaje, int TotalRequeridos, IReadOnlyList<CentroCumplimientoDto> CentrosConMenorCumplimiento)>
        CalcularCumplimientoAsync(IReadOnlyList<Guid>? centroIdsVisibles, CancellationToken cancellationToken)
    {
        var centrosQuery = centrosContext.Centros.AsQueryable();
        if (centroIdsVisibles is not null)
            centrosQuery = centrosQuery.Where(c => centroIdsVisibles.Contains(c.Id));

        var centros = await centrosQuery.Select(c => new { c.Id, c.Nombre }).ToListAsync(cancellationToken);
        if (centros.Count == 0)
            return (null, 0, []);

        var cumplimientoPorCentro = await calculoEstadoCentro.CalcularCumplimientoAsync(
            centros.Select(c => c.Id).ToList(), cancellationToken);

        var totalAlDia = cumplimientoPorCentro.Values.Sum(f => f.AlDia);
        var totalRequeridos = cumplimientoPorCentro.Values.Sum(f => f.Requeridos);
        double? porcentaje = totalRequeridos == 0 ? null : totalAlDia * 100.0 / totalRequeridos;

        var centrosConMenorCumplimiento = centros
            .Select(c => new { c.Id, c.Nombre, Fraccion = cumplimientoPorCentro.GetValueOrDefault(c.Id) })
            .Where(c => c.Fraccion is { Requeridos: > 0 })
            .OrderBy(c => c.Fraccion!.Porcentaje)
            .Take(5)
            .Select(c => new CentroCumplimientoDto(c.Id, c.Nombre, c.Fraccion!.Porcentaje!.Value))
            .ToList();

        return (porcentaje, totalRequeridos, centrosConMenorCumplimiento);
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
