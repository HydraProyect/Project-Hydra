namespace CaeManager.Application.Common;

/// <summary>
/// Puerto hacia el sistema de alertas operativas (Horizonte 2.4 del plan
/// macro: cola de IA estancada, tasa de error, latencia degradada). Mismo
/// patrón que <see cref="IEmailService"/>: Application declara el qué,
/// Infrastructure decide el cómo — la implementación real
/// (<c>SentryAlertaOperativa</c>) envía el evento a Sentry, que ya tiene
/// Better Stack enganchado para el aviso de verdad al único operador de
/// guardia (decisión ya tomada: reutilizar el canal existente, no montar uno
/// nuevo). Application no puede depender del SDK de Sentry directamente sin
/// romper <c>FronterasDeCapaTests.Application_no_depende_de_Infrastructure</c>
/// — mismo motivo por el que <see cref="Observabilidad"/> mantiene el SDK de
/// OpenTelemetry fuera de aquí.
/// </summary>
public interface IAlertaOperativa
{
    void Emitir(string mensaje, NivelAlertaOperativa nivel);

    /// <summary>
    /// Reporta una excepción real (con su stack trace) a Sentry — distinto
    /// de <see cref="Emitir"/>, que manda un mensaje agregado sin excepción
    /// asociada (tasas, cola estancada). Pensado para el "mejor esfuerzo"
    /// que ya tenían los trabajos de la cola de análisis IA
    /// (<c>ProcesadorAnalisisDocumentoHostedService</c>, Infrastructure):
    /// antes de esto, un fallo de proveedor o de lectura de archivo solo
    /// quedaba en el log local (Seq si está configurado) y no llegaba nunca
    /// a Sentry — ver D3, "un fallo de la IA deja de anunciarse como éxito".
    /// </summary>
    void CapturarExcepcion(Exception excepcion);
}

/// <summary>
/// Deliberadamente solo dos niveles: la guardia de una persona (ver
/// RUNBOOK-ALERTAS.md en el repositorio de negocio) no necesita más
/// granularidad que "esto puede esperar a mañana" frente a "esto hay que
/// mirarlo ahora".
/// </summary>
public enum NivelAlertaOperativa
{
    /// <summary>Degradación que conviene vigilar pero no impide operar (p. ej. latencia por encima de lo normal).</summary>
    Aviso,

    /// <summary>Algo que probablemente esté afectando a usuarios reales ahora mismo (cola de IA parada, tasa de error alta, backup ausente).</summary>
    Critica,
}
