namespace CaeManager.Application.Dashboard.Catalogo;

public enum CategoriaKpi
{
    Documental,
    Incidencias,
    Ia,
    Facturacion
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
}
