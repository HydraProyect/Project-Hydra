namespace CaeManager.Application.Dashboard.Catalogo;

public enum CategoriaKpi
{
    Documental,
    Evaluaciones,
    Incidencias,
    Ia,
    Facturacion
}

public enum TipoRenderKpi
{
    TileNumerico,
    TilePorcentajeConTono,
    GraficoDonut,
    GraficoBarras
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
    public const string PuntuacionMediaEvaluaciones = "eval.puntuacion-media";
    public const string CentrosConMasRiesgo = "eval.centros-riesgo";
    public const string IncidenciasAbiertas = "inc.total-abiertas";
    public const string IncidenciasPorGravedad = "inc.por-gravedad";
    public const string TiempoMedioResolucionIncidencias = "inc.tiempo-medio-resolucion-dias";
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
        new(PuntuacionMediaEvaluaciones, "Puntuación media de evaluaciones", "Media de las evaluaciones de riesgo laboral (0-100).", CategoriaKpi.Evaluaciones, TipoRenderKpi.TileNumerico),
        new(CentrosConMasRiesgo, "Centros con más riesgo", "Centros con menor puntuación media de evaluación (top 5).", CategoriaKpi.Evaluaciones, TipoRenderKpi.GraficoBarras),
        new(IncidenciasAbiertas, "Incidencias abiertas", "Incidencias operativas sin resolver.", CategoriaKpi.Incidencias, TipoRenderKpi.TileNumerico),
        new(IncidenciasPorGravedad, "Incidencias por gravedad", "Distribución de incidencias por gravedad: Leve/Grave/MuyGrave.", CategoriaKpi.Incidencias, TipoRenderKpi.GraficoBarras),
        new(TiempoMedioResolucionIncidencias, "Tiempo medio de resolución", "Días de media entre la creación de una incidencia y su resolución.", CategoriaKpi.Incidencias, TipoRenderKpi.TileNumerico),
        new(ConfianzaMediaIa, "Confianza media de extracción IA", "Confianza media de las extracciones IA del mes actual.", CategoriaKpi.Ia, TipoRenderKpi.TilePorcentajeConTono),
        new(CosteMesActualIa, "Coste IA del mes", "Coste estimado (OCR + extracción) de la IA documental este mes.", CategoriaKpi.Ia, TipoRenderKpi.TileNumerico),
        new(TiempoMedioProcesamientoIa, "Tiempo medio de procesamiento IA", "Milisegundos de media por documento procesado este mes.", CategoriaKpi.Ia, TipoRenderKpi.TileNumerico),
        new(FacturacionEstimadaMesActual, "Facturación estimada del mes", "Suma de los resúmenes de facturación estimada de los clientes con tarifas configuradas.", CategoriaKpi.Facturacion, TipoRenderKpi.TileNumerico),
    ];

    /// <summary>Paridad con el Dashboard actual — lo que ve quien no ha personalizado nada.</summary>
    public static readonly IReadOnlyList<string> KpisPorDefecto =
    [
        TrabajadoresActivos, Centros, VisitasProgramadas, SemaforoDocumental, TasaCumplimiento
    ];
}
