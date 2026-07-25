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
}
