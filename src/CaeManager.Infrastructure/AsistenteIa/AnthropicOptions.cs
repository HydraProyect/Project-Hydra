namespace CaeManager.Infrastructure.AsistenteIa;

/// <summary>
/// Configuración del chat "Pregúntale a Hydra" sobre la API de Anthropic.
/// Sin `ApiKey`, el servicio queda inerte (mismo patrón que Sentry/Backups):
/// el botón ni siquiera se muestra en la UI — ver BotonAsistenteIa.razor.
/// </summary>
public class AnthropicOptions
{
    public const string SeccionConfiguracion = "Anthropic";

    public string? ApiKey { get; set; }

    public string Modelo { get; set; } = "claude-haiku-5-5";

    /// <summary>
    /// Tope de la respuesta. El modelo razona antes de responder y ese
    /// razonamiento cuenta contra este tope aunque su texto no se devuelva:
    /// un valor ajustado a la respuesta visible la corta a medias (la API
    /// responde entonces con <c>stop_reason: "max_tokens"</c>, ver
    /// <see cref="ParadaRespuestaAnthropic"/>).
    /// </summary>
    public int MaxTokensRespuesta { get; set; } = 16000;

    /// <summary>
    /// Nivel de esfuerzo (<c>output_config.effort</c>) de las rutas de
    /// clasificación y extracción: correo, OCR, extracción estructurada y
    /// listado de trabajadores. Es el control de cuánto razona el modelo, y
    /// con él de la latencia y el coste.
    /// </summary>
    public string Esfuerzo { get; set; } = "low";

    /// <summary>Nivel de esfuerzo del chat, que responde preguntas abiertas de normativa y no una clasificación.</summary>
    public string EsfuerzoAsistente { get; set; } = "medium";

    /// <summary>
    /// Precio orientativo por millón de tokens de entrada/salida (USD),
    /// usado solo para el coste estimado de auditoría de
    /// <see cref="AnthropicDocumentAIProvider"/> (ver
    /// Project-Hydra-Negocio/tecnico/docs/ARQUITECTURA-IA-DOCUMENTAL.md § 4.2 — nunca un criterio de
    /// enrutado). Valores por defecto orientativos para Claude Haiku 5.5 con
    /// una entrada de hasta 100.000 tokens (por encima de ese tamaño la
    /// tarifa es otra); revisar si cambia el modelo o su tarifa.
    /// </summary>
    public decimal CostoPorMillonTokensEntrada { get; set; } = 0.10m;

    public decimal CostoPorMillonTokensSalida { get; set; } = 0.50m;
}
