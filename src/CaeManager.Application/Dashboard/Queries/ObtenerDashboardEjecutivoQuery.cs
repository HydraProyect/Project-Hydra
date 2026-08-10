using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using MediatR;

namespace CaeManager.Application.Dashboard.Queries;

public record ObtenerDashboardEjecutivoQuery(PeriodoKpi Periodo) : IRequest<DashboardEjecutivoDto>;

public record CentroCumplimientoMultiTenantDto(string TenantNombre, Guid CentroId, string CentroNombre, int Porcentaje);

public record DashboardEjecutivoDto(
    int TotalTenants,
    int TrabajadoresActivos,
    int Centros,
    int VisitasProgramadas,
    int VisitasUrgentes,
    int DocumentosVigentes,
    int DocumentosProximos,
    int DocumentosUrgentes,
    int DocumentosVencidos,
    int TasaCumplimiento,
    double? PorcentajeCumplimientoDocumental,
    IReadOnlyList<CentroCumplimientoMultiTenantDto> CentrosConMenorCumplimiento,
    int IncidenciasAbiertas,
    IReadOnlyList<GravedadIncidenciaConteoDto> IncidenciasPorGravedad,
    double? TiempoMedioResolucionIncidenciasDias,
    double? ConfianzaMediaIa,
    decimal CosteIaMesActual,
    double? TiempoMedioProcesamientoIaMs,
    decimal FacturacionEstimadaMesActual,
    KpisBpoDto Bpo);

/// <summary>
/// Generaliza el patrón de <c>ObtenerKpisGlobalesQuery</c> (Fase 57, Visión
/// de cartera) al catálogo completo de KPIs: por cada Cliente/tenant
/// autorizado (<see cref="ObtenerClientesAutorizadosQuery"/> — tenant de
/// origen + Delegated Workspaces activos), se ejecuta
/// <see cref="ObtenerCatalogoKpisQuery"/> con <see cref="AmbitoTenantExplicito"/>
/// fijado a ese tenant, y los resultados se fusionan **en memoria** — nunca
/// una query cruza el filtro global de tenant (ver
/// ADR-004-delegacion-consultoras-cae.md § 7.2).
///
/// El fan-out es secuencial a propósito, no <c>Task.WhenAll</c>: el
/// <c>DbContext</c> scoped no admite llamadas EF concurrentes sobre la misma
/// instancia (mismo motivo que <c>ObtenerKpisGlobalesQueryHandler</c>).
/// </summary>
public class ObtenerDashboardEjecutivoQueryHandler(IMediator mediator) : IRequestHandler<ObtenerDashboardEjecutivoQuery, DashboardEjecutivoDto>
{
    /// <summary>Cuántos centros/candidatos de riesgo se muestran en el ranking fusionado, con independencia de cuántos tenants aporten.</summary>
    public const int TopCentrosConMasRiesgo = 10;

    public async Task<DashboardEjecutivoDto> Handle(ObtenerDashboardEjecutivoQuery request, CancellationToken cancellationToken)
    {
        var clientes = await mediator.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);

        var porTenant = new List<(ClienteAutorizadoDto Cliente, CatalogoKpisValoresDto Valores)>();

        foreach (var cliente in clientes)
        {
            using (AmbitoTenantExplicito.Establecer(cliente.TenantId))
            {
                var valores = await mediator.Send(new ObtenerCatalogoKpisQuery(request.Periodo), cancellationToken);
                porTenant.Add((cliente, valores));
            }
        }

        return Fusionar(porTenant);
    }

    /// <summary>
    /// Fórmulas de merge, pura y sin dependencias de infraestructura para
    /// que se pueda probar directamente con datos fabricados: conteos se
    /// suman; tasas/medias se ponderan por el volumen (denominador) de cada
    /// tenant, nunca con un promedio simple entre tenants (un tenant con 3
    /// documentos no debe pesar igual que uno con 3000); rankings se
    /// concatenan (cada tenant ya limita su propia lista) y se recortan a
    /// <see cref="TopCentrosConMasRiesgo"/> global.
    /// </summary>
    public static DashboardEjecutivoDto Fusionar(IReadOnlyList<(ClienteAutorizadoDto Cliente, CatalogoKpisValoresDto Valores)> porTenant)
    {
        var vigentes = porTenant.Sum(p => p.Valores.Documental.DocumentosVigentes);
        var proximos = porTenant.Sum(p => p.Valores.Documental.DocumentosProximos);
        var urgentes = porTenant.Sum(p => p.Valores.Documental.DocumentosUrgentes);
        var vencidos = porTenant.Sum(p => p.Valores.Documental.DocumentosVencidos);
        var totalConVigencia = vigentes + proximos + urgentes + vencidos;
        var tasaCumplimiento = totalConVigencia == 0 ? 100 : vigentes * 100 / totalConVigencia;

        var centrosConMenorCumplimiento = porTenant
            .SelectMany(p => p.Valores.CentrosConMenorCumplimiento.Select(c =>
                new CentroCumplimientoMultiTenantDto(p.Cliente.Nombre, c.CentroId, c.CentroNombre, c.Porcentaje)))
            .OrderBy(c => c.Porcentaje)
            .Take(TopCentrosConMasRiesgo)
            .ToList();

        var incidenciasPorGravedad = porTenant
            .SelectMany(p => p.Valores.IncidenciasPorGravedad)
            .GroupBy(g => g.Gravedad)
            .Select(g => new GravedadIncidenciaConteoDto(g.Key, g.Sum(x => x.Cantidad)))
            .ToList();

        return new DashboardEjecutivoDto(
            TotalTenants: porTenant.Count,
            TrabajadoresActivos: porTenant.Sum(p => p.Valores.Documental.TrabajadoresActivos),
            Centros: porTenant.Sum(p => p.Valores.Documental.Centros),
            VisitasProgramadas: porTenant.Sum(p => p.Valores.Documental.VisitasProgramadas),
            VisitasUrgentes: porTenant.Sum(p => p.Valores.Documental.VisitasUrgentes),
            DocumentosVigentes: vigentes,
            DocumentosProximos: proximos,
            DocumentosUrgentes: urgentes,
            DocumentosVencidos: vencidos,
            TasaCumplimiento: tasaCumplimiento,
            PorcentajeCumplimientoDocumental: PromedioPonderado(
                porTenant.Select(p => (p.Valores.PorcentajeCumplimientoDocumental, (double)p.Valores.TotalRequeridosCumplimiento))),
            CentrosConMenorCumplimiento: centrosConMenorCumplimiento,
            IncidenciasAbiertas: porTenant.Sum(p => p.Valores.IncidenciasAbiertas),
            IncidenciasPorGravedad: incidenciasPorGravedad,
            TiempoMedioResolucionIncidenciasDias: PromedioPonderado(
                porTenant.Select(p => (p.Valores.TiempoMedioResolucionIncidenciasDias, (double)p.Valores.TotalIncidenciasResueltas))),
            ConfianzaMediaIa: PromedioPonderado(
                porTenant.Select(p => (p.Valores.ConfianzaMediaIa, (double)p.Valores.TotalAuditoriasIaMes))),
            CosteIaMesActual: porTenant.Sum(p => p.Valores.CosteIaMesActual),
            TiempoMedioProcesamientoIaMs: PromedioPonderado(
                porTenant.Select(p => (p.Valores.TiempoMedioProcesamientoIaMs, (double)p.Valores.TotalAuditoriasIaMes))),
            FacturacionEstimadaMesActual: porTenant.Sum(p => p.Valores.FacturacionEstimadaMesActual),
            Bpo: FusionarBpo(porTenant));
    }

    /// <summary>
    /// Los KPIs BPO son todos conteos y sumas, así que se agregan sin ponderar: los
    /// porcentajes (palanca IA, falsos avisos) se derivan al presentarlos a partir del
    /// numerador y el denominador ya sumados, que es lo correcto — sumar porcentajes de
    /// varios tenants sería exactamente el error que <see cref="PromedioPonderado"/>
    /// evita en el resto del panel.
    ///
    /// Ocupación por gestor NO se suma entre tenants: un Operador Delegado que trabaja
    /// para varios Clientes Delegantes aparecería duplicado. Se concatena y se agrupa
    /// por usuario, que es lo que hace legible la carga real de una persona.
    /// </summary>
    private static KpisBpoDto FusionarBpo(IReadOnlyList<(ClienteAutorizadoDto Cliente, CatalogoKpisValoresDto Valores)> porTenant)
    {
        var bpo = porTenant.Select(p => p.Valores.Bpo).ToList();
        if (bpo.Count == 0) return KpisBpoDto.Vacio;

        var ocupacion = bpo
            .SelectMany(b => b.OcupacionPorGestor)
            .GroupBy(o => o.UsuarioId)
            .Select(g => new OcupacionGestorDto(
                g.Key,
                g.First().Nombre,
                g.Sum(o => o.SegundosActivos),
                g.Any(o => o.PorcentajeOcupacion is not null) ? g.Sum(o => o.PorcentajeOcupacion ?? 0) : null))
            .OrderByDescending(o => o.SegundosActivos)
            .ToList();

        var horasPorCliente = bpo
            .SelectMany(b => b.HorasPorCliente)
            .GroupBy(h => h.ClienteId)
            .Select(g => new HorasClienteDto(g.Key, g.First().ClienteNombre, g.Sum(h => h.SegundosActivos)))
            .OrderByDescending(h => h.SegundosActivos)
            .Take(ObtenerKpisBpoQueryHandler.TopClientesPorHoras)
            .ToList();

        var distribucion = bpo
            .SelectMany(b => b.DistribucionAntelacion)
            .GroupBy(t => t.Tramo)
            .Select(g => new TramoAntelacionConteoDto(g.Key, g.Sum(x => x.Cantidad)))
            .OrderBy(t => t.Tramo)
            .ToList();

        var atribucion = bpo
            .SelectMany(b => b.AtribucionUrgencia)
            .GroupBy(a => a.Atribucion)
            .Select(g => new AtribucionUrgenciaConteoDto(g.Key, g.Sum(x => x.Cantidad)))
            .OrderBy(a => a.Atribucion)
            .ToList();

        return new KpisBpoDto(
            SugerenciasConfirmadasSinEdicion: bpo.Sum(b => b.SugerenciasConfirmadasSinEdicion),
            TotalSugerenciasResueltas: bpo.Sum(b => b.TotalSugerenciasResueltas),
            OcupacionPorGestor: ocupacion,
            HorasPorCliente: horasPorCliente,
            DistribucionAntelacion: distribucion,
            VisitasConFalsoAviso: bpo.Sum(b => b.VisitasConFalsoAviso),
            TotalVisitasConAntelacionMedida: bpo.Sum(b => b.TotalVisitasConAntelacionMedida),
            HorasBloqueadasPorClienteTotal: bpo.Sum(b => b.HorasBloqueadasPorClienteTotal))
        {
            AtribucionUrgencia = atribucion
        };
    }

    private static double? PromedioPonderado(IEnumerable<(double? Valor, double Peso)> valoresConPeso)
    {
        var conPeso = valoresConPeso.Where(v => v.Valor is not null && v.Peso > 0).ToList();
        var pesoTotal = conPeso.Sum(v => v.Peso);

        return pesoTotal == 0 ? null : conPeso.Sum(v => v.Valor!.Value * v.Peso) / pesoTotal;
    }
}
