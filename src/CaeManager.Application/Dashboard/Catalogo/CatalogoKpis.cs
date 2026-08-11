namespace CaeManager.Application.Dashboard.Catalogo;

public enum CategoriaKpi
{
    Documental,
    Incidencias,
    Ia,
    Facturacion,

    /// <summary>Rendimiento del equipo: cuánto multiplica la IA y cómo está repartida la carga.</summary>
    Operativa,

    /// <summary>De quién es la prisa: antelación real frente a la que el cliente cree haber dado.</summary>
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

public record DefinicionKpi(string Codigo, string Titulo, string Descripcion, CategoriaKpi Categoria, TipoRenderKpi TipoRender);

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
        new(TrabajadoresActivos, "Trabajadores activos", "Trabajadores con al menos una asignación activa.", CategoriaKpi.Documental, TipoRenderKpi.TileNumerico),
        new(Centros, "Centros", "Centros de trabajo dados de alta.", CategoriaKpi.Documental, TipoRenderKpi.TileNumerico),
        new(VisitasProgramadas, "Visitas programadas", "Visitas cuya fecha fin todavía no ha pasado.", CategoriaKpi.Documental, TipoRenderKpi.TileNumerico),
        new(VisitasUrgentes, "Gestiones urgentes (visitas)", "Visitas activas dentro de la ventana mínima de validación de la plataforma del cliente (horas de aviso configurables en Parámetros).", CategoriaKpi.Documental, TipoRenderKpi.TileNumerico),
        new(SemaforoDocumental, "Semáforo documental", "Distribución de documentos por estado: Vigente/Próximo/Urgente/Vencido.", CategoriaKpi.Documental, TipoRenderKpi.GraficoDonut),
        new(TasaCumplimiento, "Tasa de cumplimiento documental", "Porcentaje de documentos en estado Vigente sobre el total con vigencia.", CategoriaKpi.Documental, TipoRenderKpi.TilePorcentajeConTono),
        new(PorcentajeCumplimientoDocumental, "% de cumplimiento documental (trabajadores)", "Documentos obligatorios de trabajador al día sobre el total requerido, agregado de todos los centros.", CategoriaKpi.Documental, TipoRenderKpi.TilePorcentajeConTono),
        new(CentrosConMenorCumplimiento, "Centros con menor cumplimiento", "Centros con menor % de documentación obligatoria de trabajador al día (top 5).", CategoriaKpi.Documental, TipoRenderKpi.GraficoBarras),
        new(EmpresasConMasRiesgo, "Empresas con más riesgo", "Empresas con más documentos vencidos o urgentes de sus trabajadores (top 5).", CategoriaKpi.Documental, TipoRenderKpi.TablaRiesgo),
        new(IncidenciasAbiertas, "Incidencias abiertas", "Incidencias operativas sin resolver.", CategoriaKpi.Incidencias, TipoRenderKpi.TileNumerico),
        new(IncidenciasPorGravedad, "Incidencias por gravedad", "Distribución de incidencias por gravedad: Leve/Grave/MuyGrave.", CategoriaKpi.Incidencias, TipoRenderKpi.GraficoBarras),
        new(TiempoMedioResolucionIncidencias, "Tiempo medio de resolución", "Días de media entre la creación de una incidencia y su resolución.", CategoriaKpi.Incidencias, TipoRenderKpi.TileNumerico),
        new(AutomaticoVsManual, "Gestiones automáticas vs manuales", "Reparto de verificaciones IA de documentos resueltas solas frente a las que necesitaron un Gestor CAE.", CategoriaKpi.Ia, TipoRenderKpi.BarraComparativa),
        new(ConfianzaMediaIa, "Confianza media de extracción IA", "Confianza media de las extracciones IA del mes actual.", CategoriaKpi.Ia, TipoRenderKpi.TilePorcentajeConTono),
        new(CosteMesActualIa, "Coste IA del mes", "Coste estimado (OCR + extracción) de la IA documental este mes.", CategoriaKpi.Ia, TipoRenderKpi.TileNumerico),
        new(TiempoMedioProcesamientoIa, "Tiempo medio de procesamiento IA", "Milisegundos de media por documento procesado este mes.", CategoriaKpi.Ia, TipoRenderKpi.TileNumerico),
        new(FacturacionEstimadaMesActual, "Facturación estimada del mes", "Suma de los resúmenes de facturación estimada de los clientes con tarifas configuradas.", CategoriaKpi.Facturacion, TipoRenderKpi.TileNumerico),
        new(PalancaIa, "Índice de palanca IA", "Sugerencias de la IA confirmadas sin tocar ningún campo, sobre todas las resueltas este mes.", CategoriaKpi.Operativa, TipoRenderKpi.TilePorcentajeConTono),
        new(OcupacionGestores, "Ocupación por gestor", "Horas de gestión medidas este mes frente a la jornada mensual configurada. Requiere la medición de tiempo activada.", CategoriaKpi.Operativa, TipoRenderKpi.GraficoBarras),
        new(HorasPorCliente, "Horas de gestión por cliente", "Dónde se va el tiempo del equipo: horas medidas este mes por cliente (top 5).", CategoriaKpi.Operativa, TipoRenderKpi.TablaRiesgo),
        new(DistribucionAntelacion, "Distribución por tramo de antelación", "Reparto de las visitas del mes entre Estándar, Urgente y Exprés según el margen real del gestor.", CategoriaKpi.Friccion, TipoRenderKpi.GraficoDonut),
        new(FalsosAvisos, "Falsos avisos con tiempo", "Visitas avisadas con margen de sobra cuya documentación no llegó completa hasta dentro de la ventana de urgencia.", CategoriaKpi.Friccion, TipoRenderKpi.TilePorcentajeConTono),
        new(TiempoBloqueadoCliente, "Tiempo bloqueado por el cliente", "Horas de media entre que el cliente pide la visita y completa la documentación.", CategoriaKpi.Friccion, TipoRenderKpi.TileNumerico),
        new(AtribucionUrgencia, "Urgencias por atribución", "A qué se debió la prisa en cada visita: aviso tardío, documentación tardía o ninguna urgencia.", CategoriaKpi.Friccion, TipoRenderKpi.GraficoBarras),
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
