using System.Text.Json.Serialization;

namespace CaeManager.Infrastructure.AsistenteIa;

/// <summary>
/// Valores de <c>stop_reason</c> con los que la API de Anthropic responde
/// HTTP 200 sin haber completado la respuesta. Se comprueban antes de leer
/// <c>content</c>: un rechazo llega sin texto utilizable, y una respuesta
/// cortada por el tope de tokens llega con texto que parece completo y no lo
/// es — una transcripción a medias o un JSON truncado.
/// </summary>
internal static class ParadaRespuestaAnthropic
{
    /// <summary>Los clasificadores de seguridad del modelo declinaron la petición.</summary>
    public const string Rechazo = "refusal";

    /// <summary>La respuesta alcanzó <c>max_tokens</c> antes de terminar.</summary>
    public const string TopeDeTokens = "max_tokens";

    public static bool EsIncompleta(string? motivoParada) => motivoParada is Rechazo or TopeDeTokens;
}

/// <summary>Bloque <c>output_config</c> de la solicitud a la Messages API.</summary>
internal sealed record ConfiguracionSalidaAnthropic(
    [property: JsonPropertyName("effort")] string Effort);
