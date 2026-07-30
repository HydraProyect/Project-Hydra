namespace CaeManager.Infrastructure.AsistenteIa;

/// <summary>
/// Configuración de <see cref="MistralOcrDocumentAIProvider"/>. Sin
/// <see cref="ApiKey"/>, el proveedor queda inerte (mismo patrón "inerte
/// por defecto" que <see cref="AnthropicOptions"/>/<see cref="GeminiOptions"/>).
/// </summary>
public class MistralOcrOptions
{
    public const string SeccionConfiguracion = "MistralOcr";

    public string? ApiKey { get; set; }

    /// <summary>
    /// "mistral-ocr-latest" apunta siempre a la versión OCR más reciente de
    /// Mistral (OCR 4 desde el 23/06/2026: bounding boxes, tipo de bloque,
    /// confianza por palabra/página — ver docs/ARQUITECTURA-IA-DOCUMENTAL.md
    /// § 4.1) sin tener que fijar una versión numerada a mano.
    /// </summary>
    public string Modelo { get; set; } = "mistral-ocr-latest";

    /// <summary>
    /// Precio orientativo por 1.000 páginas OCR (USD), usado solo para
    /// el coste estimado de auditoría (docs/ARQUITECTURA-IA-DOCUMENTAL.md
    /// § 4.2 — nunca un criterio de enrutado). Tarifa vigente de
    /// Mistral OCR 4: $4,00/1.000 páginas. Revisar si cambia.
    /// </summary>
    public decimal CostoPorMilPaginasOcr { get; set; } = 4.00m;

    /// <summary>Modelo de chat usado para extracción estructurada (chat completions, no OCR).</summary>
    public string ModeloChat { get; set; } = "mistral-small-latest";

    public int MaxTokensRespuestaChat { get; set; } = 1024;

    /// <summary>Tarifa vigente de mistral-small-latest: $0,20/1M tokens entrada. Revisar si cambia.</summary>
    public decimal CostoPorMillonTokensEntradaChat { get; set; } = 0.20m;

    /// <summary>Tarifa vigente de mistral-small-latest: $0,60/1M tokens salida. Revisar si cambia.</summary>
    public decimal CostoPorMillonTokensSalidaChat { get; set; } = 0.60m;
}
