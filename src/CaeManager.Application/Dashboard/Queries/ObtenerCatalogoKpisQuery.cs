using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.DocumentosIa;
using CaeManager.Application.Facturacion;
using CaeManager.Application.Incidencias;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Facturacion;
using CaeManager.Domain.Incidencias;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Dashboard.Queries;

public record ObtenerCatalogoKpisQuery(PeriodoKpi Periodo) : IRequest<CatalogoKpisValoresDto>;

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
    decimal FacturacionEstimadaMesActual,
    KpisBpoDto Bpo,
    /// <summary>
    /// Límite blando configurado en <see cref="Domain.Configuracion.ParametroSistema.PresupuestoMensualIaUsd"/>
    /// para este tenant — <c>null</c> si no se ha configurado ninguno (aviso apagado).
    /// </summary>
    decimal? PresupuestoMensualIaUsd = null)
{
    /// <summary>Aviso al operador (Horizonte 2.7): true cuando hay presupuesto configurado y el gasto del período lo supera. No bloquea nada — solo enciende el aviso visual del Dashboard Ejecutivo.</summary>
    public bool PresupuestoIaExcedido => PresupuestoMensualIaUsd is { } presupuesto && CosteIaMesActual > presupuesto;
}

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
public class ObtenerCatalogoKpisQueryHandler(
    ICentrosQueryContext centrosContext, IDocumentosIaQueryContext documentosIaContext,
    ICalculoEstadoCentroService calculoEstadoCentro, IFacturacionQueryContext facturacionContext,
    IIncidenciasQueryContext incidenciasContext, IAlcanceDatosService alcanceDatos, IMediator mediator,
    IAsignacionesQueryContext asignacionesContext, IDocumentosQueryContext documentosContext,
    IProyectosQueryContext proyectosContext, ITrabajadoresQueryContext trabajadoresContext,
    IVisitasQueryContext visitasContext, IConfiguracionQueryContext configuracionContext)
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

        var (confianzaMedia, costeMes, tiempoMedioMs, totalAuditoriasMes) = await CalcularIaAsync(request.Periodo, cancellationToken);

        var facturacionEstimada = await CalcularFacturacionEstimadaAsync(request.Periodo, cancellationToken);

        var bpo = await mediator.Send(new ObtenerKpisBpoQuery(request.Periodo), cancellationToken);

        var presupuestoMensualIaUsd = (await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken)).PresupuestoMensualIaUsd;

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
            FacturacionEstimadaMesActual: facturacionEstimada,
            Bpo: bpo,
            PresupuestoMensualIaUsd: presupuestoMensualIaUsd);
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
        CalcularIaAsync(PeriodoKpi periodo, CancellationToken cancellationToken)
    {
        var auditoriasQuery = documentosIaContext.AuditoriasExtraccionIa
            .Where(a => a.CreadaEnUtc >= periodo.InicioUtc && a.CreadaEnUtc < periodo.FinUtc);

        var totalAuditoriasMes = await auditoriasQuery.CountAsync(cancellationToken);
        var costeMes = await auditoriasQuery.SumAsync(a => (a.CosteEstimado ?? 0) + (a.CosteEstimadoOcr ?? 0), cancellationToken);

        if (totalAuditoriasMes == 0) return (null, costeMes, null, 0);

        var confianzaMedia = await auditoriasQuery.AverageAsync(a => (double)a.ConfianzaGeneral, cancellationToken);
        var tiempoMedioMs = await auditoriasQuery.AverageAsync(a => (double)a.TiempoProcesamientoMs, cancellationToken);

        return (confianzaMedia, costeMes, tiempoMedioMs, totalAuditoriasMes);
    }

    /// <summary>
    /// Suma el resumen de facturación estimada del período para todos los Clientes que
    /// tienen al menos una <c>TarifaCliente</c> configurada.
    ///
    /// Antes llamaba a <see cref="ObtenerResumenFacturacionQuery"/> (varias sub-queries
    /// cada una) una vez por Cliente-con-tarifa y por mes del periodo — un Dashboard
    /// Ejecutivo de una consultora con varios tenants, cada uno con varios clientes
    /// facturables, disparaba cientos de idas y vueltas a la base de datos en una sola
    /// carga de página (Horizonte 2.7, revisión de N+1 en las vistas de la ventana
    /// 73-93). Aquí se recalculan las mismas 7 fórmulas de <c>ConceptoFacturable</c> que
    /// esa Query, pero agrupadas por cliente en una sola consulta por concepto y mes —
    /// igual "para toda la página en vez de una consulta por fila" que
    /// <c>ObtenerEmpresasQuery.CalcularCumplimientoPorEmpresaAsync</c> — en vez de una
    /// consulta por cliente. El cálculo por cliente individual (pantalla de Facturación)
    /// sigue viviendo, sin tocar, en <see cref="ObtenerResumenFacturacionQuery"/>.
    /// </summary>
    private async Task<decimal> CalcularFacturacionEstimadaAsync(PeriodoKpi periodo, CancellationToken cancellationToken)
    {
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);

        var tarifasQuery = facturacionContext.TarifasCliente.AsQueryable();
        if (clienteIdsVisibles is not null)
            tarifasQuery = tarifasQuery.Where(t => clienteIdsVisibles.Contains(t.ClienteId));

        var tarifas = await tarifasQuery
            .Select(t => new { t.ClienteId, t.Concepto, t.PrecioUnitario })
            .ToListAsync(cancellationToken);

        if (tarifas.Count == 0) return 0m;

        var clienteIds = tarifas.Select(t => t.ClienteId).Distinct().ToList();
        var conceptosUsados = tarifas.Select(t => t.Concepto).Distinct().ToList();

        var centros = await centrosContext.Centros
            .Where(c => clienteIds.Contains(c.ClienteId))
            .Select(c => new { c.Id, c.ClienteId })
            .ToListAsync(cancellationToken);
        var clientePorCentro = centros.ToDictionary(c => c.Id, c => c.ClienteId);
        var centroIds = centros.Select(c => c.Id).ToList();

        // Igual que ObtenerResumenFacturacionQuery: un periodo arbitrario se resuelve
        // sumando los meses naturales que toca (se prefiere sumar de más y decirlo a
        // inventar un prorrateo que nadie ha decidido).
        var unidadesPorClienteYConcepto = new Dictionary<(Guid ClienteId, ConceptoFacturable Concepto), int>();

        foreach (var (anyo, mes) in periodo.MesesQueAbarca())
        {
            var inicio = new DateTime(anyo, mes, 1, 0, 0, 0, DateTimeKind.Utc);
            var fin = inicio.AddMonths(1);
            var inicioFecha = DateOnly.FromDateTime(inicio);
            var finFecha = DateOnly.FromDateTime(fin.AddDays(-1));

            foreach (var concepto in conceptosUsados)
            {
                var porCliente = concepto switch
                {
                    ConceptoFacturable.TrabajadorActivo =>
                        await ContarTrabajadoresActivosPorClienteAsync(centroIds, clientePorCentro, inicioFecha, finFecha, cancellationToken),
                    ConceptoFacturable.AltaCentro =>
                        await ContarAltasCentroPorClienteAsync(clienteIds, inicio, fin, cancellationToken),
                    ConceptoFacturable.VisitaTrabajadorExtranjero =>
                        await ContarVisitasExtranjeroPorClienteAsync(centroIds, clientePorCentro, inicioFecha, finFecha, cancellationToken),
                    ConceptoFacturable.DocumentoGestionado =>
                        await ContarDocumentosGestionadosPorClienteAsync(centroIds, clientePorCentro, inicioFecha, finFecha, inicio, fin, cancellationToken),
                    ConceptoFacturable.TecnicoAsignadoProyecto =>
                        await ContarTecnicosAsignadosProyectoPorClienteAsync(clienteIds, inicioFecha, finFecha, cancellationToken),
                    ConceptoFacturable.GestionProyectoRealizada =>
                        await ContarGestionesProyectoPorClienteAsync(clienteIds, inicio, fin, cancellationToken),
                    ConceptoFacturable.DiaProyectoAbierto =>
                        await ContarDiasProyectoAbiertoPorClienteAsync(clienteIds, inicioFecha, finFecha, cancellationToken),
                    _ => new Dictionary<Guid, int>()
                };

                foreach (var (clienteId, unidades) in porCliente)
                {
                    var clave = (clienteId, concepto);
                    unidadesPorClienteYConcepto[clave] = unidadesPorClienteYConcepto.GetValueOrDefault(clave) + unidades;
                }
            }
        }

        return tarifas.Sum(t => unidadesPorClienteYConcepto.GetValueOrDefault((t.ClienteId, t.Concepto)) * t.PrecioUnitario);
    }

    private async Task<Dictionary<Guid, int>> ContarTrabajadoresActivosPorClienteAsync(
        IReadOnlyList<Guid> centroIds, IReadOnlyDictionary<Guid, Guid> clientePorCentro,
        DateOnly inicio, DateOnly fin, CancellationToken cancellationToken)
    {
        if (centroIds.Count == 0) return new Dictionary<Guid, int>();

        var filas = await asignacionesContext.Asignaciones
            .Where(a => centroIds.Contains(a.CentroId) && a.FechaAlta <= fin && (a.FechaBaja == null || a.FechaBaja >= inicio))
            .Select(a => new { a.CentroId, a.TrabajadorId })
            .Distinct()
            .ToListAsync(cancellationToken);

        return filas
            .GroupBy(f => clientePorCentro[f.CentroId])
            .ToDictionary(g => g.Key, g => g.Select(x => x.TrabajadorId).Distinct().Count());
    }

    private async Task<Dictionary<Guid, int>> ContarAltasCentroPorClienteAsync(
        IReadOnlyList<Guid> clienteIds, DateTime inicio, DateTime fin, CancellationToken cancellationToken) =>
        (await centrosContext.Centros
            .Where(c => clienteIds.Contains(c.ClienteId) && c.CreadoEnUtc >= inicio && c.CreadoEnUtc < fin)
            .GroupBy(c => c.ClienteId)
            .Select(g => new { ClienteId = g.Key, Cantidad = g.Count() })
            .ToListAsync(cancellationToken))
            .ToDictionary(x => x.ClienteId, x => x.Cantidad);

    private async Task<Dictionary<Guid, int>> ContarVisitasExtranjeroPorClienteAsync(
        IReadOnlyList<Guid> centroIds, IReadOnlyDictionary<Guid, Guid> clientePorCentro,
        DateOnly inicio, DateOnly fin, CancellationToken cancellationToken)
    {
        if (centroIds.Count == 0) return new Dictionary<Guid, int>();

        var visitas = await visitasContext.Visitas
            .Where(v => centroIds.Contains(v.CentroId) && v.FechaInicio >= inicio && v.FechaInicio <= fin)
            .Select(v => new { v.Id, v.CentroId })
            .ToListAsync(cancellationToken);

        if (visitas.Count == 0) return new Dictionary<Guid, int>();

        var visitaIds = visitas.Select(v => v.Id).ToList();
        var centroPorVisita = visitas.ToDictionary(v => v.Id, v => v.CentroId);

        var filas = await visitasContext.VisitasTrabajadores
            .Where(vt => visitaIds.Contains(vt.VisitaId))
            .Join(trabajadoresContext.Trabajadores, vt => vt.TrabajadorId, t => t.Id, (vt, t) => new { vt.VisitaId, t.Dni })
            .Distinct()
            .ToListAsync(cancellationToken);

        // Trabajadores con NIE, TIE o documento extranjero (no DNI español).
        return filas
            .Where(f => ValidadorIdentificacion.Analizar(f.Dni).Tipo != TipoIdentificacion.Dni)
            .GroupBy(f => clientePorCentro[centroPorVisita[f.VisitaId]])
            .ToDictionary(g => g.Key, g => g.Select(x => x.Dni).Distinct().Count());
    }

    private async Task<Dictionary<Guid, int>> ContarDocumentosGestionadosPorClienteAsync(
        IReadOnlyList<Guid> centroIds, IReadOnlyDictionary<Guid, Guid> clientePorCentro,
        DateOnly inicioFecha, DateOnly finFecha, DateTime inicio, DateTime fin, CancellationToken cancellationToken)
    {
        if (centroIds.Count == 0) return new Dictionary<Guid, int>();

        // Join (no atribución por diccionario) a propósito: si un mismo Trabajador
        // estuvo asignado a Centros de más de un Cliente dentro del período, su
        // documento cuenta para cada uno de esos Clientes — igual que si se llamara
        // a ObtenerResumenFacturacionQuery por separado para cada Cliente.
        var pares = await (
            from asignacion in asignacionesContext.Asignaciones
            where centroIds.Contains(asignacion.CentroId)
                && asignacion.FechaAlta <= finFecha
                && (asignacion.FechaBaja == null || asignacion.FechaBaja >= inicioFecha)
            join documento in documentosContext.Documentos
                on asignacion.TrabajadorId equals documento.TrabajadorId
            where documento.CreadoEnUtc >= inicio && documento.CreadoEnUtc < fin
            select new { asignacion.CentroId, DocumentoId = documento.Id })
            .Distinct()
            .ToListAsync(cancellationToken);

        return pares
            .GroupBy(p => clientePorCentro[p.CentroId])
            .ToDictionary(g => g.Key, g => g.Select(x => x.DocumentoId).Distinct().Count());
    }

    private async Task<Dictionary<Guid, int>> ContarTecnicosAsignadosProyectoPorClienteAsync(
        IReadOnlyList<Guid> clienteIds, DateOnly inicio, DateOnly fin, CancellationToken cancellationToken) =>
        (await (
            from proyecto in proyectosContext.Proyectos
            where clienteIds.Contains(proyecto.ClienteId)
            join tecnico in proyectosContext.ProyectosTecnicos on proyecto.Id equals tecnico.ProyectoId
            where tecnico.FechaAlta <= fin && (tecnico.FechaBaja == null || tecnico.FechaBaja >= inicio)
            select new { proyecto.ClienteId, tecnico.TrabajadorId })
            .Distinct()
            .ToListAsync(cancellationToken))
            .GroupBy(x => x.ClienteId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.TrabajadorId).Distinct().Count());

    private async Task<Dictionary<Guid, int>> ContarGestionesProyectoPorClienteAsync(
        IReadOnlyList<Guid> clienteIds, DateTime inicio, DateTime fin, CancellationToken cancellationToken)
    {
        var proyectos = await proyectosContext.Proyectos
            .Where(p => clienteIds.Contains(p.ClienteId))
            .Select(p => new { p.Id, p.ClienteId })
            .ToListAsync(cancellationToken);

        if (proyectos.Count == 0) return new Dictionary<Guid, int>();

        var proyectoIds = proyectos.Select(p => p.Id).ToList();
        var clientePorProyecto = proyectos.ToDictionary(p => p.Id, p => p.ClienteId);

        var documentos = await documentosContext.Documentos
            .Where(d => d.ProyectoId != null && proyectoIds.Contains(d.ProyectoId!.Value) && d.CreadoEnUtc >= inicio && d.CreadoEnUtc < fin)
            .Select(d => d.ProyectoId!.Value)
            .ToListAsync(cancellationToken);

        return documentos
            .GroupBy(p => clientePorProyecto[p])
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>Suma de días en memoria (sin agregación en SQL posible: cada Proyecto intersecta el período de forma distinta) tras traer los datos de todos los Clientes en una sola consulta.</summary>
    private async Task<Dictionary<Guid, int>> ContarDiasProyectoAbiertoPorClienteAsync(
        IReadOnlyList<Guid> clienteIds, DateOnly inicio, DateOnly fin, CancellationToken cancellationToken)
    {
        var proyectos = await proyectosContext.Proyectos
            .Where(p => clienteIds.Contains(p.ClienteId))
            .Select(p => new { p.ClienteId, p.FechaInicio, p.FechaCierreReal })
            .ToListAsync(cancellationToken);

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var resultado = new Dictionary<Guid, int>();

        foreach (var proyecto in proyectos)
        {
            var aperturaFin = proyecto.FechaCierreReal ?? hoy;
            var desde = proyecto.FechaInicio > inicio ? proyecto.FechaInicio : inicio;
            var hasta = aperturaFin < fin ? aperturaFin : fin;

            if (hasta < desde) continue;

            var dias = hasta.DayNumber - desde.DayNumber + 1;
            resultado[proyecto.ClienteId] = resultado.GetValueOrDefault(proyecto.ClienteId) + dias;
        }

        return resultado;
    }
}
