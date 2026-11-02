using CaeManager.Domain.Common;
using CaeManager.Application.Alertas;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.EstadoMaterializado;
using CaeManager.Application.Proyectos;
using CaeManager.Application.TiposDocumento;
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
    int ParesExigidosSinDocumento = 0,
    bool TienePendientesDocumentales = false,
    /// <summary>Documentos de Empresa vencidos, con la selección de Alertas. Fuera de la tasa, que mide Documentos de Trabajador.</summary>
    int DocumentosEmpresaVencidos = 0,
    /// <summary>Documentos de Empresa en estado Urgente, con la selección de Alertas.</summary>
    int DocumentosEmpresaUrgentes = 0,
    /// <summary>Documentos de Empresa en estado Próximo, con la selección de Alertas.</summary>
    int DocumentosEmpresaProximos = 0,
    /// <summary>Clientes empresariales del alcance. Inicio ofrece «Desglosar por Cliente» solo con más de uno.</summary>
    int ClientesEmpresarialesEnAlcance = 0,
    /// <summary>Documentos de Empresa vigentes, con la selección de Alertas sin cota de fecha. Fuera de la tasa.</summary>
    int DocumentosEmpresaVigentes = 0)
{
    /// <summary>
    /// Vencidos de la Empresa gestionada tal como los cuentan Alertas y Mi trabajo: de Trabajador y de Empresa. Es la cifra
    /// por Tenant de Inicio y de Visión de cartera; <see cref="DocumentosVencidos"/> solo es la parte que entra en la tasa.
    /// </summary>
    public int VencidosComoEnAlertas => DocumentosVencidos + DocumentosEmpresaVencidos;

    /// <summary>Urgentes de Trabajador y de Empresa, como en Alertas y en Mi trabajo («Por vencer»).</summary>
    public int UrgentesComoEnAlertas => DocumentosUrgentes + DocumentosEmpresaUrgentes;

    /// <summary>Próximos de Trabajador y de Empresa, como en Alertas.</summary>
    public int ProximosComoEnAlertas => DocumentosProximos + DocumentosEmpresaProximos;

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
/// resultados distintos. Los 4 contadores de documentos y la tasa solo cuentan
/// Documentos de Trabajador, con la misma selección que Alertas
/// (<see cref="DocumentosConAlertaDeVigencia.DeTrabajador"/>); los vencimientos de
/// Empresa van aparte (<c>DocumentosEmpresa*</c>) y se suman en la cifra por Tenant
/// (<see cref="KpisDashboardDto.VencidosComoEnAlertas"/>), que así es la de Alertas y Mi trabajo.
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
public class ObtenerKpisDashboardQueryHandler(ICentrosQueryContext centrosContext, IConfiguracionQueryContext configuracionContext, IDocumentosQueryContext documentosContext, ITrabajadoresQueryContext trabajadoresContext, IVisitasQueryContext visitasContext, IAlcanceDatosService alcanceDatos, ICalculoEstadoCentroService calculoEstadoCentro, IEvaluacionDeAccesoPorCentroService evaluacionDeAcceso, IEmpresasQueryContext empresasContext, ITiposDocumentoQueryContext tiposDocumentoContext, IProyectosQueryContext proyectosContext, ILectorEstadoCentros? lectorEstadoCentros = null)
    : IRequestHandler<ObtenerKpisDashboardQuery, KpisDashboardDto>
{
    /// <summary>
    /// El estado de cada Centro del alcance: el guardado en <c>EstadosEntidad</c> cuando el Tenant está completo y la fila es
    /// fiable (<see cref="ILectorEstadoCentros"/>, la misma lectura y las mismas guardas que el listado de Centros), y el cálculo
    /// de siempre para los demás. Los KPI solo cuentan estados, así que el detalle de las causas no hace falta.
    /// </summary>
    private async Task<IReadOnlyList<EstadoCentro>> EstadosDeCentrosAsync(List<Guid> centroIds, CancellationToken cancellationToken)
    {
        if (centroIds.Count == 0)
            return [];

        var leidos = lectorEstadoCentros is null
            ? new Dictionary<Guid, EstadoCentroLeido>()
            : await lectorEstadoCentros.LeerAsync(centroIds, cancellationToken);
        var estados = leidos.Values.Select(l => l.Estado).ToList();

        var sinFilaFiable = centroIds.Where(id => !leidos.ContainsKey(id)).ToList();
        if (sinFilaFiable.Count > 0)
            estados.AddRange((await calculoEstadoCentro.CalcularAsync(sinFilaFiable, cancellationToken)).Values.Select(r => r.Estado));
        return estados;
    }

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

        var (anyoActual, mesActual) = CorteDeMesDeNegocio.MesDe(DateTime.UtcNow);
        var inicioDeMes = CorteDeMesDeNegocio.InicioEnUtc(anyoActual, mesActual);
        var trabajadoresNuevosEsteMes = await trabajadoresQuery.CountAsync(t => t.CreadoEnUtc >= inicioDeMes, cancellationToken);

        var centrosQuery = centrosContext.Centros.AsQueryable();
        if (centroIdsVisibles is not null) centrosQuery = centrosQuery.Where(c => centroIdsVisibles.Contains(c.Id));
        var centroIds = await centrosQuery.Select(c => c.Id).ToListAsync(cancellationToken);
        var centros = centroIds.Count;

        // Cuántos Clientes empresariales hay en el alcance: «Desglosar por Cliente» solo tiene sentido con más de uno (un
        // usuario de Cliente empresarial ve siempre el suyo). Con alcance acotado es su lista; sin acotar, los Clientes
        // empresariales con algún Centro de Trabajo.
        var clientesEmpresariales = clienteIdsVisibles?.Count
            ?? await centrosQuery.Select(c => c.ClienteId).Distinct().CountAsync(cancellationToken);

        // D-7: una acreditación Rechazada aplicable (y cualquier otra causa
        // bloqueante) pone el Centro en Bloqueado. Mismo servicio, sin copiar
        // la lista de causas, y solo sobre los Centros del alcance ya filtrado.
        var estadosCentro = await EstadosDeCentrosAsync(centroIds, cancellationToken);
        var centrosBloqueados = estadosCentro.Count(e => e == EstadoCentro.Bloqueado);

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

        var hoy = DiaDeNegocio.Hoy();

        // La misma selección que Alertas (y, a través de ella, Mi trabajo): sin documentos de un Trabajador que ya no
        // existe ni de un voluntario inactivo. Antes Inicio contaba su propio universo y un mismo Tenant beneficiario daba
        // otra cifra de vencidos que Alertas y Mi trabajo.
        var documentosQuery = DocumentosConAlertaDeVigencia.DeTrabajador(
            documentosContext, trabajadoresContext, tiposDocumentoContext, proyectosContext, trabajadorIdsVisibles, hoy);

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
        // Centros del alcance, incluida la herencia global. No es el porcentaje del Centro (que cuenta solo
        // inclusiones explícitas): esta lectura detecta documentos faltantes. Solo se consulta en ese caso.
        var paresExigidosSinDocumento = fraccion.Requeridos == 0 && centroIds.Count > 0
            ? (await calculoEstadoCentro.ObtenerParesExigidosAsync(centroIds, cancellationToken)).Count
            : 0;
        var (tasa, sinDatos) = KpisDashboardDto.SinDocumentos(fraccion, paresExigidosSinDocumento);
        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        // Los documentos de Empresa, con la misma selección que Alertas sin cota de fecha: no entran en la tasa (que mide
        // Documentos de Trabajador), pero sí en todos los segmentos del semáforo ejecutivo.
        var vencimientosEmpresa = await DocumentosConAlertaDeVigencia.DeEmpresa(
                documentosContext, empresasContext, tiposDocumentoContext, empresaIdsVisibles)
            .GroupBy(d => d.FechaVencimiento)
            .Select(g => new { FechaVencimiento = g.Key, Cantidad = g.Count() })
            .ToListAsync(cancellationToken);
        var empresaPorEstado = vencimientosEmpresa
            .GroupBy(v => CalculadoraEstadoDocumento.Calcular(
                VigenciaDocumento.VenceEl(v.FechaVencimiento!.Value), hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias))
            .ToDictionary(g => g.Key, g => g.Sum(v => v.Cantidad));
        var empresasConAlertas = empresaPorEstado.Any(e => e.Key is EstadoDocumento.Vencido or EstadoDocumento.Urgente or EstadoDocumento.Proximo);

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
            ParesExigidosSinDocumento: paresExigidosSinDocumento,
            TienePendientesDocumentales: empresasConAlertas
                || estadosCentro.Any(e => e is not (EstadoCentro.Vigente or EstadoCentro.SinGestionCae)),
            DocumentosEmpresaVencidos: empresaPorEstado.GetValueOrDefault(EstadoDocumento.Vencido),
            DocumentosEmpresaUrgentes: empresaPorEstado.GetValueOrDefault(EstadoDocumento.Urgente),
            DocumentosEmpresaProximos: empresaPorEstado.GetValueOrDefault(EstadoDocumento.Proximo),
            ClientesEmpresarialesEnAlcance: clientesEmpresariales,
            DocumentosEmpresaVigentes: empresaPorEstado.GetValueOrDefault(EstadoDocumento.Vigente));
    }
}
