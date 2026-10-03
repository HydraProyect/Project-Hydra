using CaeManager.Domain.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Centros;
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
    /// <summary>
    /// Solo documental: los Documentos de Trabajador al día según
    /// <see cref="CumplimientoDocumental"/> (Vigente, Próximo, Urgente y Sin caducidad) sobre todos los
    /// Documentos de Trabajador, histórico incluido (<see cref="Fraccion"/>). NO es un veredicto de cumplimiento — no
    /// ve los Centros de Trabajo bloqueados (<see cref="CentrosBloqueados"/>), y sin ningún documento vale 100 porque
    /// no hay nada que contar (<see cref="SinDatos"/>, <see cref="SinCarteraAsignada"/>). Su universo (todos los
    /// documentos, no los pares que exigen los Centros) no es ninguno de los cuatro contextos de
    /// <see cref="ContextoCumplimiento"/>: queda pendiente de decisión del propietario.
    /// </summary>
    int TasaCumplimientoDocumental,
    int VisitasUrgentes = 0,
    bool SinCarteraAsignada = false,
    /// <summary>Trabajadores dados de alta desde el día 1 del mes en curso (mockup Inicio TALVEG, pista del KPI "Trabajadores activos") — EntidadBase.CreadoEnUtc, sin campo nuevo.</summary>
    int TrabajadoresNuevosEsteMes = 0,
    /// <summary>
    /// Centros de Trabajo del alcance cuyo estado, calculado con
    /// <see cref="ICalculoEstadoCentroService"/> (el mismo criterio que la tabla
    /// de Centros, el Centro 360 y la cola de /bandeja), es
    /// <see cref="EstadoCentro.Bloqueado"/>: entre otras causas, una
    /// acreditación Rechazada por la plataforma y aplicable a ese Centro
    /// (D-7 del piloto Outbound). Con uno o más, el Tenant no está al día
    /// aunque <see cref="TasaCumplimientoDocumental"/> sea alta.
    /// </summary>
    int CentrosBloqueados = 0,
    /// <summary>
    /// Ni un Centro de Trabajo ni un Documento con fecha de vencimiento en el
    /// alcance: el 100 de <see cref="TasaCumplimientoDocumental"/> es «nada que
    /// medir», nunca «al día», y no se presenta como organización en verde.
    /// </summary>
    bool SinDatos = false,
    /// <summary>Documentos de Trabajador sin vigencia confirmada: cuentan en el denominador de la tasa y no en el numerador.</summary>
    int DocumentosSinConfirmar = 0,
    /// <summary>Documentos de Trabajador confirmados como que no caducan: cuentan en el numerador y en el denominador de la tasa.</summary>
    int DocumentosSinCaducidad = 0)
{
    /// <summary>La fracción de la tasa, por la única definición de <see cref="CumplimientoDocumental"/>.</summary>
    public FraccionCumplimiento Fraccion => FraccionDe(
        DocumentosVigentes, DocumentosProximos, DocumentosUrgentes, DocumentosVencidos, DocumentosSinConfirmar, DocumentosSinCaducidad);

    /// <summary>La misma fracción desde los recuentos por estado: el Dashboard Ejecutivo la usa con los recuentos sumados de varios Tenants.</summary>
    public static FraccionCumplimiento FraccionDe(
        int vigentes, int proximos, int urgentes, int vencidos, int sinConfirmar, int sinCaducidad) =>
        CumplimientoDocumental.Evaluar(
        [
            (EstadoDocumento.Vigente, vigentes), (EstadoDocumento.Proximo, proximos),
            (EstadoDocumento.Urgente, urgentes), (EstadoDocumento.Vencido, vencidos),
            (EstadoDocumento.SinConfirmar, sinConfirmar), (EstadoDocumento.SinCaducidad, sinCaducidad)
        ]);
}

/// <summary>
/// Los seis KPI del Dashboard (ver Project-Hydra-Negocio/tecnico/DATABASE.md, hoja "Dashboard" del Excel
/// original). El semáforo de cada documento se calcula en memoria con
/// CalculadoraEstadoDocumento — la misma función que usan las tablas de
/// Documentos — para que Dashboard y detalle nunca puedan mostrar
/// resultados distintos. Los 4 contadores de documentos solo cuentan
/// Documentos de Trabajador — los de Cliente/Empresa quedan fuera de estos
/// KPI por ahora (fuera de alcance).
///
/// <para>
/// El porcentaje es documental y no decide si el Tenant está al día: los
/// Centros de Trabajo bloqueados se cuentan aparte
/// (<see cref="KpisDashboardDto.CentrosBloqueados"/>) con
/// <see cref="ICalculoEstadoCentroService"/> sobre los Centros del alcance, en
/// una sola llamada por lotes — su número de consultas no crece con el de
/// Centros (<c>KpisCentrosBloqueadosBajoRlsTests</c>).
/// </para>
/// </summary>
public class ObtenerKpisDashboardQueryHandler(ICentrosQueryContext centrosContext, IConfiguracionQueryContext configuracionContext, IDocumentosQueryContext documentosContext, ITrabajadoresQueryContext trabajadoresContext, IVisitasQueryContext visitasContext, IAlcanceDatosService alcanceDatos, ICalculoEstadoCentroService calculoEstadoCentro)
    : IRequestHandler<ObtenerKpisDashboardQuery, KpisDashboardDto>
{
    public async Task<KpisDashboardDto> Handle(ObtenerKpisDashboardQuery request, CancellationToken cancellationToken)
    {
        // Un rol restringido (GestorCae/CoordinadorCae/Cliente) sin ningún
        // Cliente asignado en la cartera actual devuelve lista vacía (nunca
        // null, ver IAlcanceDatosService) — hay que distinguirlo del "todo
        // vigente" para no mostrar SLA 100% en verde sobre un alcance vacío.
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        var sinCarteraAsignada = clienteIdsVisibles is { Count: 0 };

        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);

        var trabajadoresQuery = trabajadoresContext.Trabajadores.AsQueryable();
        if (trabajadorIdsVisibles is not null) trabajadoresQuery = trabajadoresQuery.Where(t => trabajadorIdsVisibles.Contains(t.Id));
        var trabajadoresActivos = await trabajadoresQuery.CountAsync(cancellationToken);

        var inicioDeMes = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var trabajadoresNuevosEsteMes = await trabajadoresQuery.CountAsync(t => t.CreadoEnUtc >= inicioDeMes, cancellationToken);

        var centrosQuery = centrosContext.Centros.AsQueryable();
        if (centroIdsVisibles is not null) centrosQuery = centrosQuery.Where(c => centroIdsVisibles.Contains(c.Id));
        var centroIds = await centrosQuery.Select(c => c.Id).ToListAsync(cancellationToken);
        var centros = centroIds.Count;

        // D-7: una acreditación Rechazada aplicable (y cualquier otra causa
        // bloqueante) pone el Centro en Bloqueado. Mismo servicio, sin copiar
        // la lista de causas, y solo sobre los Centros del alcance ya filtrado.
        var estadosCentro = await calculoEstadoCentro.CalcularAsync(centroIds, cancellationToken);
        var centrosBloqueados = estadosCentro.Values.Count(r => r.Estado == EstadoCentro.Bloqueado);

        var hoyParaVisitas = DiaDeNegocio.Hoy();
        var visitasQuery = visitasContext.Visitas.Where(v => !v.EstaCancelada && v.FechaFin >= hoyParaVisitas); // FS-11: una cancelada no se cuenta
        if (centroIdsVisibles is not null) visitasQuery = visitasQuery.Where(v => centroIdsVisibles.Contains(v.CentroId));
        var visitasProgramadas = await visitasQuery.CountAsync(cancellationToken);

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);

        // Fase F: mismo criterio SQL que ObtenerVisitasQuery(SoloUrgentes=true) — ver el comentario de CalculadoraUrgenciaVisita.
        var limiteAvisoVisita = hoyParaVisitas.AddDays(parametros.HorasAvisoVisita / 24);
        var visitasUrgentes = await visitasQuery.CountAsync(v => v.FechaInicio <= limiteAvisoVisita, cancellationToken);

        var documentosQuery = documentosContext.Documentos.Operativos().Where(d => d.TrabajadorId != null);
        if (trabajadorIdsVisibles is not null) documentosQuery = documentosQuery.Where(d => trabajadorIdsVisibles.Contains(d.TrabajadorId!.Value));

        // Agregado en PostgreSQL: una fila por par distinto (EstadoVigencia,
        // FechaVencimiento) con su recuento, no una por Documento — el volumen
        // ya no crece con el histórico documental del Tenant (P1-D1,
        // ConsultasKpiAcotadasBajoRlsTests). El estado de cada par lo sigue
        // decidiendo CalculadoraEstadoDocumento, así que la clasificación es la
        // misma por construcción.
        var vigencias = await documentosQuery
            .GroupBy(d => new { d.EstadoVigencia, d.FechaVencimiento })
            .Select(g => new { g.Key.EstadoVigencia, g.Key.FechaVencimiento, Cantidad = g.Count() })
            .ToListAsync(cancellationToken);

        var hoy = DiaDeNegocio.Hoy();

        var documentosPorEstado = vigencias
            .GroupBy(v => CalculadoraEstadoDocumento.Calcular(
                v.EstadoVigencia, v.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias))
            .ToDictionary(g => g.Key, g => g.Sum(v => v.Cantidad));

        var vigentes = documentosPorEstado.GetValueOrDefault(EstadoDocumento.Vigente);
        var proximos = documentosPorEstado.GetValueOrDefault(EstadoDocumento.Proximo);
        var urgentes = documentosPorEstado.GetValueOrDefault(EstadoDocumento.Urgente);
        var vencidos = documentosPorEstado.GetValueOrDefault(EstadoDocumento.Vencido);
        var sinConfirmar = documentosPorEstado.GetValueOrDefault(EstadoDocumento.SinConfirmar);
        var sinCaducidad = documentosPorEstado.GetValueOrDefault(EstadoDocumento.SinCaducidad);

        // Qué cuenta como al día lo decide CumplimientoDocumental (Próximo y Urgente sí; Sin confirmar no, y entra en
        // el denominador; Sin caducidad en los dos lados). Esta tasa no tiene fórmula propia.
        var fraccion = KpisDashboardDto.FraccionDe(vigentes, proximos, urgentes, vencidos, sinConfirmar, sinCaducidad);

        return new KpisDashboardDto(
            TrabajadoresActivos: trabajadoresActivos,
            Centros: centros,
            DocumentosVencidos: vencidos,
            DocumentosUrgentes: urgentes,
            DocumentosProximos: proximos,
            DocumentosVigentes: vigentes,
            VisitasProgramadas: visitasProgramadas,
            TasaCumplimientoDocumental: fraccion.Porcentaje ?? 100,
            VisitasUrgentes: visitasUrgentes,
            SinCarteraAsignada: sinCarteraAsignada,
            TrabajadoresNuevosEsteMes: trabajadoresNuevosEsteMes,
            CentrosBloqueados: centrosBloqueados,
            SinDatos: centros == 0 && fraccion.Requeridos == 0,
            DocumentosSinConfirmar: sinConfirmar,
            DocumentosSinCaducidad: sinCaducidad);
    }
}
