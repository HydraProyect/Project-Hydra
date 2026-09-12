namespace CaeManager.Application.Dashboard.Catalogo;

public enum CategoriaKpi
{
    Documental,
    Incidencias,
    Ia,
    Facturacion,

    /// <summary>Rendimiento del equipo: cuánto multiplica la IA y cómo está repartida la carga.</summary>
    Operativa,

    /// <summary>De quién es la prisa: antelación real frente a la que el Cliente empresarial cree haber dado.</summary>
    Friccion
}

public enum TipoRenderKpi
{
    TileNumerico,
    TilePorcentajeConTono,
    GraficoDonut,
    GraficoBarras,
    TablaRiesgo,
    BarraComparativa
}

/// <summary>
/// Sobre qué ventana de tiempo se calcula un KPI. No es decoración: el
/// Dashboard Ejecutivo tiene un selector de periodo arriba, así que cualquier
/// cifra se lee como «del periodo» salvo que diga lo contrario. Medido en
/// <c>ObtenerCatalogoKpisQueryHandler.Handle</c>: <c>request.Periodo</c> solo
/// llega a <c>CalcularIaAsync</c>, <c>CalcularFacturacionEstimadaAsync</c> y
/// <c>ObtenerKpisBpoQuery</c>.
/// </summary>
public enum AlcanceTemporalKpi
{
    /// <summary>Se calcula sobre el <see cref="PeriodoKpi"/> elegido.</summary>
    Periodo,

    /// <summary>Foto de hoy: ninguna consulta lo filtra por fechas, y no acumula historia (documentos por estado, incidencias sin resolver).</summary>
    EstadoActual,

    /// <summary>Todo lo registrado desde el principio: ninguna consulta lo filtra por fechas y la historia entera cuenta (incidencias por gravedad, verificaciones resueltas).</summary>
    Acumulado
}

public record DefinicionKpi(
    string Codigo, string Titulo, string Descripcion, CategoriaKpi Categoria, TipoRenderKpi TipoRender,
    AlcanceTemporalKpi AlcanceTemporal);

/// <summary>
/// Rótulo de una categoría tal y como se lee en pantalla. El nombre del miembro
/// del enum no sirve: <c>Ia</c> y <c>Facturacion</c> se leerían sin la mayúscula
/// ni el acento que llevan en castellano, y el mockup Gen 2 los pinta como «IA»
/// y «Facturación». Vive aquí, junto al catálogo, y no en la página, porque el
/// rótulo es parte de la definición del KPI, no de su maquetación.
/// </summary>
public static class EtiquetasCategoriaKpi
{
    public static string De(CategoriaKpi categoria) => categoria switch
    {
        CategoriaKpi.Ia => "IA",
        CategoriaKpi.Facturacion => "Facturación",
        CategoriaKpi.Friccion => "Fricción",
        _ => categoria.ToString()
    };
}

/// <summary>
/// Catálogo v1 de KPIs disponibles para el Dashboard Ejecutivo — lista
/// cerrada en código (YAGNI, no tabla editable en BD). Los códigos son
/// claves estables: se persisten en <c>PreferenciaDashboardUsuario.CodigosKpiSeleccionados</c>,
/// así que renombrarlos invalida las preferencias ya guardadas (tratarlos
/// como se trataría un nombre de columna).
/// </summary>
public static class CatalogoKpis
{
    public const string TrabajadoresActivos = "doc.trabajadores-activos";
    public const string Centros = "doc.centros";
    public const string VisitasProgramadas = "doc.visitas-programadas";
    public const string VisitasUrgentes = "doc.visitas-urgentes";
    public const string SemaforoDocumental = "doc.semaforo-documental";
    public const string TasaCumplimiento = "doc.tasa-cumplimiento";
    public const string PorcentajeCumplimientoDocumental = "doc.pct-cumplimiento-trabajadores";
    public const string CentrosConMenorCumplimiento = "doc.centros-menor-cumplimiento";
    public const string EmpresasConMasRiesgo = "doc.empresas-mas-riesgo";
    public const string IncidenciasAbiertas = "inc.total-abiertas";
    public const string IncidenciasPorGravedad = "inc.por-gravedad";
    public const string TiempoMedioResolucionIncidencias = "inc.tiempo-medio-resolucion-dias";
    public const string AutomaticoVsManual = "ia.automatico-vs-manual";
    public const string ConfianzaMediaIa = "ia.confianza-media";
    public const string CosteMesActualIa = "ia.coste-mes-actual";
    public const string TiempoMedioProcesamientoIa = "ia.tiempo-medio-procesamiento-ms";
    public const string FacturacionEstimadaMesActual = "fact.estimado-mes-actual";
    public const string PalancaIa = "ope.palanca-ia";
    public const string OcupacionGestores = "ope.ocupacion-gestores";
    public const string HorasPorCliente = "ope.horas-por-cliente";
    public const string DistribucionAntelacion = "fric.distribucion-antelacion";
    public const string FalsosAvisos = "fric.falsos-avisos";
    public const string TiempoBloqueadoCliente = "fric.tiempo-bloqueado-cliente";
    public const string AtribucionUrgencia = "fric.atribucion-urgencia";

    public static readonly IReadOnlyList<DefinicionKpi> Todos =
    [
        new(TrabajadoresActivos, "Trabajadores activos", "Trabajadores con al menos una asignación activa.", CategoriaKpi.Documental, TipoRenderKpi.TileNumerico, AlcanceTemporalKpi.EstadoActual),
        new(Centros, "Centros", "Centros de trabajo dados de alta.", CategoriaKpi.Documental, TipoRenderKpi.TileNumerico, AlcanceTemporalKpi.EstadoActual),
        new(VisitasProgramadas, "Visitas programadas", "Visitas cuya fecha fin todavía no ha pasado.", CategoriaKpi.Documental, TipoRenderKpi.TileNumerico, AlcanceTemporalKpi.EstadoActual),
        new(VisitasUrgentes, "Gestiones urgentes (visitas)", "Visitas activas dentro de la ventana mínima de validación de la Plataforma CAE del Cliente empresarial (horas de aviso configurables en Parámetros).", CategoriaKpi.Documental, TipoRenderKpi.TileNumerico, AlcanceTemporalKpi.EstadoActual),
        new(SemaforoDocumental, "Semáforo documental", "Distribución de documentos por estado: Vigente/Próximo/Urgente/Vencido.", CategoriaKpi.Documental, TipoRenderKpi.GraficoDonut, AlcanceTemporalKpi.EstadoActual),
        new(TasaCumplimiento, "Tasa de cumplimiento documental", "Porcentaje de documentos en estado Vigente sobre el total con vigencia.", CategoriaKpi.Documental, TipoRenderKpi.TilePorcentajeConTono, AlcanceTemporalKpi.EstadoActual),
        new(PorcentajeCumplimientoDocumental, "% de cumplimiento documental (trabajadores)", "Documentos de trabajador que se piden y están al día sobre el total que se pide, agregado de todos los centros.", CategoriaKpi.Documental, TipoRenderKpi.TilePorcentajeConTono, AlcanceTemporalKpi.EstadoActual),
        new(CentrosConMenorCumplimiento, "Centros con menor cumplimiento", "Centros con menor % de documentación de trabajador que se pide y está al día (top 5).", CategoriaKpi.Documental, TipoRenderKpi.GraficoBarras, AlcanceTemporalKpi.EstadoActual),
        new(EmpresasConMasRiesgo, "Empresas con más riesgo", "Empresas con más documentos vencidos o urgentes de sus trabajadores (top 5).", CategoriaKpi.Documental, TipoRenderKpi.TablaRiesgo, AlcanceTemporalKpi.EstadoActual),
        new(IncidenciasAbiertas, "Incidencias abiertas", "Incidencias operativas sin resolver.", CategoriaKpi.Incidencias, TipoRenderKpi.TileNumerico, AlcanceTemporalKpi.EstadoActual),
        new(IncidenciasPorGravedad, "Incidencias por gravedad", "Distribución de incidencias por gravedad: Leve, Grave o Muy grave.", CategoriaKpi.Incidencias, TipoRenderKpi.GraficoBarras, AlcanceTemporalKpi.Acumulado),
        new(TiempoMedioResolucionIncidencias, "Tiempo medio de resolución", "Días de media entre la creación de una incidencia y su resolución.", CategoriaKpi.Incidencias, TipoRenderKpi.TileNumerico, AlcanceTemporalKpi.Acumulado),
        new(AutomaticoVsManual, "Gestiones automáticas vs manuales", "Reparto de verificaciones IA de documentos resueltas solas frente a las que necesitaron un Gestor CAE.", CategoriaKpi.Ia, TipoRenderKpi.BarraComparativa, AlcanceTemporalKpi.Acumulado),
        new(ConfianzaMediaIa, "Confianza media de extracción IA", "Confianza media de las extracciones IA del mes actual.", CategoriaKpi.Ia, TipoRenderKpi.TilePorcentajeConTono, AlcanceTemporalKpi.Periodo),
        new(CosteMesActualIa, "Coste IA del mes", "Coste estimado (OCR + extracción) de la IA documental este mes.", CategoriaKpi.Ia, TipoRenderKpi.TileNumerico, AlcanceTemporalKpi.Periodo),
        new(TiempoMedioProcesamientoIa, "Tiempo medio de procesamiento IA", "Milisegundos de media por documento procesado este mes.", CategoriaKpi.Ia, TipoRenderKpi.TileNumerico, AlcanceTemporalKpi.Periodo),
        new(FacturacionEstimadaMesActual, "Facturación estimada del mes", "Suma de los resúmenes de facturación estimada de los Clientes empresariales con tarifas configuradas.", CategoriaKpi.Facturacion, TipoRenderKpi.TileNumerico, AlcanceTemporalKpi.Periodo),
        new(PalancaIa, "Índice de palanca IA", "Sugerencias de la IA confirmadas sin tocar ningún campo, sobre todas las resueltas este mes.", CategoriaKpi.Operativa, TipoRenderKpi.TilePorcentajeConTono, AlcanceTemporalKpi.Periodo),
        new(OcupacionGestores, "Ocupación por Gestor CAE", "Horas de gestión medidas este mes frente a la jornada mensual configurada. Requiere la medición de tiempo activada.", CategoriaKpi.Operativa, TipoRenderKpi.GraficoBarras, AlcanceTemporalKpi.Periodo),
        new(HorasPorCliente, "Horas de gestión por Cliente empresarial", "Dónde se va el tiempo del equipo: horas medidas este mes por Cliente empresarial (top 5).", CategoriaKpi.Operativa, TipoRenderKpi.TablaRiesgo, AlcanceTemporalKpi.Periodo),
        new(DistribucionAntelacion, "Distribución por tramo de antelación", "Reparto de las visitas del mes entre Estándar, Urgente y Exprés según el margen real del Gestor CAE.", CategoriaKpi.Friccion, TipoRenderKpi.GraficoDonut, AlcanceTemporalKpi.Periodo),
        new(FalsosAvisos, "Falsos avisos con tiempo", "Visitas avisadas con margen de sobra cuya documentación no llegó completa hasta dentro de la ventana de urgencia.", CategoriaKpi.Friccion, TipoRenderKpi.TilePorcentajeConTono, AlcanceTemporalKpi.Periodo),
        new(TiempoBloqueadoCliente, "Tiempo bloqueado por el Cliente empresarial", "Horas de media entre que el Cliente empresarial pide la visita y completa la documentación.", CategoriaKpi.Friccion, TipoRenderKpi.TileNumerico, AlcanceTemporalKpi.Periodo),
        new(AtribucionUrgencia, "Urgencias por atribución", "A qué se debió la prisa en cada visita: aviso tardío, documentación tardía o ninguna urgencia.", CategoriaKpi.Friccion, TipoRenderKpi.GraficoBarras, AlcanceTemporalKpi.Periodo),
    ];

    /// <summary>
    /// Paridad con el Dashboard actual — lo que ve quien no ha personalizado nada.
    /// Incluye EmpresasConMasRiesgo y AutomaticoVsManual (docs/blueprints/OPERATIONAL-HOME.md
    /// § 7): en el Dashboard actual eran fijos, no elegibles, para quien tuviera el rol — y todo
    /// el que llega a Dashboard Ejecutivo ya cumple ese rol, así que el valor por defecto los
    /// mantiene visibles sin que el usuario tenga que ir a buscarlos al panel de Personalizar.
    /// </summary>
    public static readonly IReadOnlyList<string> KpisPorDefecto =
    [
        TrabajadoresActivos, Centros, VisitasProgramadas, SemaforoDocumental, TasaCumplimiento,
        EmpresasConMasRiesgo, AutomaticoVsManual
    ];

    /// <summary>
    /// Qué ve cada perfil antes de personalizar nada. No es autorización — el catálogo
    /// completo sigue disponible en "Personalizar" para quien llega a esta pantalla —,
    /// solo un punto de partida sensato: Dirección mira dinero y fricción, Coordinación
    /// mira reparto de carga y urgencias, y el Gestor mira lo suyo.
    /// </summary>
    public static IReadOnlyList<string> KpisPorDefectoPorRol(string? rol) => rol switch
    {
        "DireccionCae" =>
        [
            .. KpisPorDefecto, FacturacionEstimadaMesActual, DistribucionAntelacion, FalsosAvisos, OcupacionGestores
        ],
        "CoordinadorCae" =>
        [
            .. KpisPorDefecto, OcupacionGestores, HorasPorCliente, DistribucionAntelacion, PalancaIa
        ],
        "GestorCae" =>
        [
            .. KpisPorDefecto, PalancaIa, OcupacionGestores
        ],
        _ => KpisPorDefecto
    };
}
