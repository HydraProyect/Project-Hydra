using System.Text.Json.Serialization;

namespace CaeManager.Infrastructure.AsistenteIa;

/// <summary>
/// Valores de <c>stop_reason</c> de la API de Anthropic. Se comprueban antes
/// de leer <c>content</c>, porque la API responde HTTP 200 también cuando no
/// completa la respuesta: un rechazo llega sin texto utilizable, y una
/// respuesta cortada llega con texto que parece completo y no lo es — una
/// transcripción a medias o un JSON truncado.
/// </summary>
internal static class ParadaRespuestaAnthropic
{
    /// <summary>El modelo terminó la respuesta por sí mismo.</summary>
    public const string FinDeTurno = "end_turn";

    /// <summary>El modelo emitió una de las secuencias de parada pedidas.</summary>
    public const string SecuenciaDeParada = "stop_sequence";

    /// <summary>Los clasificadores de seguridad del modelo declinaron la petición.</summary>
    public const string Rechazo = "refusal";

    /// <summary>
    /// Lista blanca, no negra: solo los dos motivos de fin normal cuentan
    /// como respuesta completa. Cualquier otro —el tope de tokens, un rechazo,
    /// la ventana de contexto agotada, o uno que la API añada más adelante—
    /// se trata como incompleto en vez de darse por bueno por omisión. La
    /// ausencia del campo no se interpreta: no dice nada sobre la respuesta.
    /// </summary>
    public static bool EsIncompleta(string? motivoParada) =>
        motivoParada is not (null or FinDeTurno or SecuenciaDeParada);
}

/// <summary>Bloque <c>output_config</c> de la solicitud a la Messages API.</summary>
internal sealed record ConfiguracionSalidaAnthropic(
    [property: JsonPropertyName("effort")] string Effort)
{
    /// <summary>
    /// Sin esfuerzo configurado no hay bloque: la solicitud sale sin
    /// <c>output_config</c> y la API aplica el nivel por omisión del modelo.
    /// </summary>
    public static ConfiguracionSalidaAnthropic? De(string? esfuerzo) =>
        string.IsNullOrWhiteSpace(esfuerzo) ? null : new ConfiguracionSalidaAnthropic(esfuerzo);
}
