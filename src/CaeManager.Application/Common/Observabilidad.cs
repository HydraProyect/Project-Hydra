using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CaeManager.Application.Common;

/// <summary>
/// Punto único de instrumentación de OpenTelemetry para toda la aplicación
/// (Horizonte 2.3 del plan macro, sobre lo que
/// <see cref="LoggingBehavior{TRequest,TResponse}"/> ya anticipaba como
/// P3-30). Vive en Application —no en Infrastructure ni en Web— porque solo
/// usa los tipos de <c>System.Diagnostics(.Metrics)</c> que trae el
/// framework compartido de cualquier proyecto net10.0 sin paquete NuGet
/// adicional: ni esta clase ni quien la usa (LoggingBehavior, el procesador
/// de la cola de IA, el CircuitHandler de Blazor) dependen del SDK de
/// OpenTelemetry en sí, así que no violan la regla de arquitectura de que
/// Application no dependa de Infrastructure ni de un proveedor concreto
/// (ver FronterasDeCapaTests). El SDK de verdad (paquetes
/// <c>OpenTelemetry.*</c>), la instrumentación de ASP.NET Core/HTTP/EF Core
/// y el exportador OTLP hacia Seq se configuran solo en CaeManager.Web
/// (Program.cs) y se suscriben a este <see cref="ActivitySource"/>/Meter por
/// nombre. Si esa suscripción no existe (mismo "inerte por defecto" que el
/// resto de la observabilidad de esta app: sin "Serilog:Seq:ServerUrl" no
/// hay ningún listener), <c>StartActivity</c> devuelve null y las métricas
/// de aquí no tienen a quién exportar — el coste es marginal, no una
/// llamada de red.
/// </summary>
public static class Observabilidad
{
    /// <summary>Nombre único del ActivitySource y del Meter — es lo que Program.cs suscribe con AddSource/AddMeter.</summary>
    public const string NombreOrigen = "CaeManager";

    public static readonly ActivitySource ActivitySource = new(NombreOrigen);

    private static readonly Meter Meter = new(NombreOrigen);

    /// <summary>
    /// Latencia por Command/Query de MediatR — la misma medición que
    /// <see cref="LoggingBehavior{TRequest,TResponse}"/> ya hacía solo para
    /// el log, ahora también como histograma consultable (p95 por comando,
    /// alertas de 2.4). Etiquetado solo por "Request" (el tipo del
    /// comando/query, cardinalidad acotada a los ~150 handlers/queries que
    /// documenta LoggingBehavior) — nunca por TenantId/UsuarioId, que sí van
    /// en el span pero no en la métrica, para no disparar la cardinalidad de
    /// las series temporales.
    /// </summary>
    public static readonly Histogram<double> DuracionComandoMs = Meter.CreateHistogram<double>(
        "caemanager.comando.duracion_ms",
        unit: "ms",
        description: "Latencia de un Command/Query de MediatR (LoggingBehavior).");

    /// <summary>
    /// Un documento cuyo <c>TrabajoAnalisisDocumento</c> terminó en
    /// Completado (ver ProcesadorAnalisisDocumentoHostedService). "Documentos
    /// procesados/hora" del plan es la tasa de este contador vista en el
    /// backend (Seq) — no algo que esta app calcule y guarde ella misma.
    /// </summary>
    public static readonly Counter<long> DocumentosProcesados = Meter.CreateCounter<long>(
        "caemanager.documentos.procesados",
        description: "Documentos con un TrabajoAnalisisDocumento completado con éxito, por tipo de análisis.");

    /// <summary>
    /// Peticiones HTTP salientes a un proveedor de IA, etiquetadas por host.
    /// Cuenta <b>intentos</b>, no operaciones: un reintento suma otra, porque el
    /// proveedor puede haber procesado (y cobrado) la petición cuya respuesta se
    /// perdió — que es justo lo que hay que poder ver.
    ///
    /// Sin esto, el gasto real solo se conocía a posteriori por la factura. La
    /// <c>AuditoriaExtraccionIa</c> registra el coste estimado del
    /// procesamiento, pero no ve los intentos que el pipeline de resiliencia
    /// hace por debajo, así que un documento que se reintentaba varias veces
    /// aparecía como una sola extracción.
    ///
    /// Etiquetada por host y no por tenant a propósito: el host tiene
    /// cardinalidad acotada (tres proveedores), mientras que el tenant la haría
    /// crecer con cada cliente nuevo. Para atribuir gasto por tenant hace falta
    /// el presupuesto que todavía no existe, no una etiqueta más.
    /// </summary>
    public static readonly Counter<long> LlamadasProveedorIa = Meter.CreateCounter<long>(
        "caemanager.ia.llamadas",
        description: "Peticiones HTTP enviadas a un proveedor de IA, reintentos incluidos, por host.");

    private static long _colaIaProfundidad;

    /// <summary>
    /// "Profundidad de cola de IA" del plan: cuántos <c>TrabajoAnalisisDocumento</c>
    /// siguen Pendiente o Procesando en este instante, sumado entre todos los
    /// tenants activos. Un <see cref="ObservableGauge{T}"/> exige un callback
    /// síncrono y esto exige una consulta a BD, así que el valor se cachea
    /// aquí — <see cref="ActualizarColaIaProfundidad"/> lo actualiza en cada
    /// ciclo de sondeo de ProcesadorAnalisisDocumentoHostedService (cada 5 s,
    /// antes de procesar nada, para medir el atasco real y no el que queda
    /// tras vaciar la cola) y el gauge solo lee la caché cuando el
    /// exportador de métricas la pide.
    /// </summary>
    public static readonly ObservableGauge<long> ColaIaProfundidad = Meter.CreateObservableGauge(
        "caemanager.cola_ia.profundidad",
        () => Interlocked.Read(ref _colaIaProfundidad),
        description: "TrabajoAnalisisDocumento en estado Pendiente o Procesando, todos los tenants activos.");

    public static void ActualizarColaIaProfundidad(long valor) => Interlocked.Exchange(ref _colaIaProfundidad, valor);

    private static long _circuitosActivos;

    /// <summary>
    /// "Circuitos activos" del plan: circuitos de Blazor Server con conexión
    /// SignalR abierta ahora mismo — la señal que decide, según el umbral ya
    /// fijado en el ADR-008 de Horizonte 2.1 (≥80 sostenidos), cuándo activar
    /// la multi-réplica que ya existe construida. <see cref="CircuitoAbierto"/>/
    /// <see cref="CircuitoCerrado"/> los llama el CircuitHandler registrado
    /// en Program.cs (OnCircuitOpenedAsync/OnCircuitClosedAsync) — un
    /// circuito reconectado tras una desconexión (ver las CircuitOptions de
    /// Horizonte 2.1, también en Program.cs) pasa primero por cerrado y
    /// vuelve a abrirse, así que la cuenta no se desincroniza.
    /// </summary>
    public static readonly ObservableGauge<long> CircuitosActivos = Meter.CreateObservableGauge(
        "caemanager.circuitos.activos",
        () => Interlocked.Read(ref _circuitosActivos),
        description: "Circuitos de Blazor Server con conexión SignalR abierta.");

    public static void CircuitoAbierto() => Interlocked.Increment(ref _circuitosActivos);

    public static void CircuitoCerrado() => Interlocked.Decrement(ref _circuitosActivos);
}
