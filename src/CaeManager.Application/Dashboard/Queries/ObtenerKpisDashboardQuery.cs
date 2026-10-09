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
    /// ve los Trabajadores bloqueados (<see cref="TrabajadoresBloqueados"/>) ni los Centros de Trabajo con bloqueo de la plataforma
    /// (<see cref="CentrosBloqueados"/>). Su universo (todos los
    /// documentos, no los pares que exigen los Centros) no es ninguno de los cuatro contextos de
    /// <see cref="ContextoCumplimiento"/>: queda pendiente de decisión del propietario.
    ///
    /// <para>
    /// Sin ningún Documento de Trabajador en el alcance no hay fracción que calcular y decide
    /// <see cref="SinDocumentos"/>, con lo que enseñan Centros y Empresas para los mismos datos (decisión del propietario,
    /// 2026-10-09: «que no dé 100 %»): si los Centros del alcance exigen algún par, vale 0 — todo falta —; si no exigen
    /// ninguno, no hay nada que medir (<see cref="SinDatos"/>) y el 100 que queda es un valor neutro que no se pinta, igual
    /// que con <see cref="SinCarteraAsignada"/>.
    /// </para>
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
    /// <see cref="EstadoCentro.Bloqueado"/>. Desde 2026-10-03 solo lo causa la plataforma del Cliente empresarial: una
    /// acreditación vencida allí o Rechazada por ella y aplicable a ese Centro (D-7 del piloto Outbound); los documentos
    /// de Trabajador y de Empresa bloquean a Trabajadores, no al Centro (<see cref="TrabajadoresBloqueados"/>). Con uno o
    /// más, el Tenant no está al día aunque <see cref="TasaCumplimientoDocumental"/> sea alta.
    /// </summary>
    int CentrosBloqueados = 0,
    /// <summary>
    /// Ni un Documento de Trabajador ni un par exigido por los Centros de Trabajo del
    /// alcance (lo que incluye no tener ningún Centro): el 100 de <see cref="TasaCumplimientoDocumental"/> es «nada que
    /// medir», nunca «al día», y no se presenta como organización en verde. Es la misma lectura que el <c>null</c> de
    /// <see cref="FraccionCumplimiento.Porcentaje"/> en Centros y Empresas.
    /// </summary>
    bool SinDatos = false,
    /// <summary>Documentos de Trabajador sin vigencia confirmada: cuentan en el denominador de la tasa y no en el numerador.</summary>
    int DocumentosSinConfirmar = 0,
    /// <summary>Documentos de Trabajador confirmados como que no caducan: cuentan en el numerador y en el denominador de la tasa.</summary>
    int DocumentosSinCaducidad = 0,
    /// <summary>
    /// Trabajadores distintos, del alcance, bloqueados en al menos un Centro por un documento bloqueante ausente o que ya no vale
    /// con las condiciones de ese Centro (<see cref="IEvaluacionDeAccesoPorCentroService"/>: la misma regla y los mismos datos que
    /// Mi trabajo). «Bloqueado» es un estado del Trabajador, nunca del Centro (2026-10-03). Con uno o más, el Tenant no está al día
    /// aunque <see cref="TasaCumplimientoDocumental"/> sea alta.
    /// </summary>
    int TrabajadoresBloqueados = 0,
    /// <summary>
    /// Pares Trabajador × Tipo de documento que exigen los Centros de Trabajo del alcance (contexto
    /// <see cref="ContextoCumplimiento.Centro"/>) cuando en él no hay ningún Documento de Trabajador: todos faltan y son el
    /// «de cuántos» del 0 de <see cref="TasaCumplimientoDocumental"/>. Vale 0 en cuanto existe algún documento — la tasa
    /// sale entonces de <see cref="Fraccion"/> y esos pares no se consultan.
    /// </summary>
    int ParesExigidosSinDocumento = 0)
{
    /// <summary>La fracción de la tasa, por la única definición de <see cref="CumplimientoDocumental"/>.</summary>
    public FraccionCumplimiento Fraccion => FraccionDe(
        DocumentosVigentes, DocumentosProximos, DocumentosUrgentes, DocumentosVencidos, DocumentosSinConfirmar, DocumentosSinCaducidad);

    /// <summary>
    /// El «de cuántos» de <see cref="TasaCumplimientoDocumental"/>: los Documentos de Trabajador medidos o, sin ninguno, los
    /// pares que exigen los Centros (<see cref="ParesExigidosSinDocumento"/>). Es lo que pesa esta organización en la media
    /// de Visión de cartera: una tasa que se pinta pesa por su propio denominador.
    /// </summary>
    public int DenominadorDeLaTasa => Fraccion.Requeridos + ParesExigidosSinDocumento;

    /// <summary>La misma fracción desde los recuentos por estado: el Dashboard Ejecutivo la usa con los recuentos sumados de varios Tenants.</summary>
    public static FraccionCumplimiento FraccionDe(
        int vigentes, int proximos, int urgentes, int vencidos, int sinConfirmar, int sinCaducidad) =>
        CumplimientoDocumental.Evaluar(
        [
            (EstadoDocumento.Vigente, vigentes), (EstadoDocumento.Proximo, proximos),
            (EstadoDocumento.Urgente, urgentes), (EstadoDocumento.Vencido, vencidos),
            (EstadoDocumento.SinConfirmar, sinConfirmar), (EstadoDocumento.SinCaducidad, sinCaducidad)
        ]);

    /// <summary>
    /// La tasa y <see cref="SinDatos"/> cuando el alcance no tiene ningún Documento de Trabajador
    /// (<paramref name="fraccion"/> sin requeridos). Con documentos, la tasa es la de la fracción y esto no decide nada.
    /// </summary>
    /// <param name="fraccion">La fracción de los Documentos de Trabajador del alcance.</param>
    /// <param name="paresExigidos">Pares que exigen los Centros del alcance; solo se consulta sin documentos.</param>
    public static (int Tasa, bool SinDatos) SinDocumentos(FraccionCumplimiento fraccion, int paresExigidos) =>
        fraccion.Porcentaje is { } porcentaje ? (porcentaje, false)
        : paresExigidos > 0 ? (0, false)
        : (100, true);
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
/// Trabajadores bloqueados (<see cref="KpisDashboardDto.TrabajadoresBloqueados"/>, con
/// <see cref="IEvaluacionDeAccesoPorCentroService"/>) y los Centros de Trabajo con bloqueo de la plataforma
/// (<see cref="KpisDashboardDto.CentrosBloqueados"/>, con <see cref="ICalculoEstadoCentroService"/>) se cuentan aparte sobre los
/// Centros del alcance, en una sola llamada por lotes cada uno — su número de consultas no crece con el de
/// Centros (<c>KpisCentrosBloqueadosBajoRlsTests</c>).
/// </para>
/// </summary>
public class ObtenerKpisDashboardQueryHandler(ICentrosQueryContext centrosContext, IConfiguracionQueryContext configuracionContext, IDocumentosQueryContext documentosContext, ITrabajadoresQueryContext trabajadoresContext, IVisitasQueryContext visitasContext, IAlcanceDatosService alcanceDatos, ICalculoEstadoCentroService calculoEstadoCentro, IEvaluacionDeAccesoPorCentroService evaluacionDeAcceso)
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

        // Trabajadores bloqueados: la regla única por Centro, sobre los Centros del alcance. Un Trabajador bloqueado en dos
        // Centros cuenta una vez.
        var evaluacion = centroIds.Count == 0
            ? EvaluacionDeAccesoPorCentro.Vacia
            : await evaluacionDeAcceso.EvaluarAsync(centroIds, cancellationToken);
        var trabajadoresBloqueados = evaluacion.Requisitos
            .Where(r => ReglaBloqueoDeAcceso.Bloquea(r.Resultado.Situacion))
            .Select(r => r.TrabajadorId)
            .Distinct()
            .Count();

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

        // Sin ningún Documento de Trabajador la fracción no dice nada: lo que falta lo dicen los pares que exigen los
        // Centros del alcance, con el mismo cálculo que el listado de Centros. Solo se consulta en ese caso.
        var paresExigidosSinDocumento = fraccion.Requeridos == 0 && centroIds.Count > 0
            ? (await calculoEstadoCentro.CalcularCumplimientoAsync(centroIds, cancellationToken)).Values.Sum(f => f.Requeridos)
            : 0;
        var (tasa, sinDatos) = KpisDashboardDto.SinDocumentos(fraccion, paresExigidosSinDocumento);

        return new KpisDashboardDto(
            TrabajadoresActivos: trabajadoresActivos,
            Centros: centros,
            DocumentosVencidos: vencidos,
            DocumentosUrgentes: urgentes,
            DocumentosProximos: proximos,
            DocumentosVigentes: vigentes,
            VisitasProgramadas: visitasProgramadas,
            TasaCumplimientoDocumental: tasa,
            VisitasUrgentes: visitasUrgentes,
            SinCarteraAsignada: sinCarteraAsignada,
            TrabajadoresNuevosEsteMes: trabajadoresNuevosEsteMes,
            CentrosBloqueados: centrosBloqueados,
            SinDatos: sinDatos,
            DocumentosSinConfirmar: sinConfirmar,
            DocumentosSinCaducidad: sinCaducidad,
            TrabajadoresBloqueados: trabajadoresBloqueados,
            ParesExigidosSinDocumento: paresExigidosSinDocumento);
    }
}
