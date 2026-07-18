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
}
