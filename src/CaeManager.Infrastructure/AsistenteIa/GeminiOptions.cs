namespace CaeManager.Infrastructure.AsistenteIa;

/// <summary>
/// Configuración de <see cref="GeminiDocumentAIProvider"/>. Sin
/// <see cref="ApiKey"/>, el proveedor queda inerte (mismo patrón "inerte
/// por defecto" que <see cref="AnthropicOptions"/>): el router lo ignora
/// porque <c>ExtraerEstructuradoAsync</c> devuelve un fallo controlado, no
/// porque no esté registrado.
/// </summary>
public class GeminiOptions
{
    public const string SeccionConfiguracion = "Gemini";

    public string? ApiKey { get; set; }

    /// <summary>
    /// Gemini 2.5 Flash se retira el 16/10/2026 (Google ya lo anuncia) —
    /// no se usa aquí ni como valor por defecto ni de referencia. 3.5 Flash
    /// es la opción sin fecha de retirada anunciada a fecha de esta
    /// implementación (2026-07). Revisar si cambia el catálogo de modelos.
    /// </summary>
    public string Modelo { get; set; } = "gemini-3.5-flash";

    public int MaxTokensRespuesta { get; set; } = 1024;

    /// <summary>
    /// Precio orientativo por millón de tokens de entrada/salida (USD),
    /// usado solo para el coste estimado de auditoría (ver
    /// docs/ARQUITECTURA-IA-DOCUMENTAL.md § 4.2 — nunca un criterio de
    /// enrutado). Valores de Gemini 3.5 Flash, más caro que el 2.5 Flash ya
    /// retirado — revisar si cambia el modelo o su tarifa.
    /// </summary>
    public decimal CostoPorMillonTokensEntrada { get; set; } = 1.50m;

    public decimal CostoPorMillonTokensSalida { get; set; } = 9.00m;
}
