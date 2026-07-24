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

    public string Modelo { get; set; } = "claude-sonnet-5";

    public int MaxTokensRespuesta { get; set; } = 1024;

    /// <summary>
    /// Precio orientativo por millón de tokens de entrada/salida (USD),
    /// usado solo para el coste estimado de auditoría de
    /// <see cref="AnthropicDocumentAIProvider"/> (ver
    /// docs/ARQUITECTURA-IA-DOCUMENTAL.md § 4.2 — nunca un criterio de
    /// enrutado). Valores por defecto orientativos para Claude Sonnet;
    /// revisar si cambia el modelo o su tarifa.
    /// </summary>
    public decimal CostoPorMillonTokensEntrada { get; set; } = 3m;

    public decimal CostoPorMillonTokensSalida { get; set; } = 15m;
}
