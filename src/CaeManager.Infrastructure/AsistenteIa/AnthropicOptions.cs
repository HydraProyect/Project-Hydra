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

    /// <summary>Modelo de toda ruta que no fije el suyo en <see cref="Rutas"/>.</summary>
    public string Modelo { get; set; } = "claude-sonnet-5";

    /// <summary>
    /// Tope de la respuesta. El modelo razona antes de responder y ese
    /// razonamiento cuenta contra este tope aunque su texto no se devuelva:
    /// un valor ajustado a la respuesta visible la corta a medias (la API
    /// responde entonces con <c>stop_reason: "max_tokens"</c>, ver
    /// <see cref="ParadaRespuestaAnthropic"/>).
    /// </summary>
    public int MaxTokensRespuesta { get; set; } = 16000;

    /// <summary>
    /// Nivel de esfuerzo (<c>output_config.effort</c>) de toda ruta que no
    /// fije el suyo en <see cref="Rutas"/>. Es el control de cuánto razona el
    /// modelo, y con él de la latencia y el coste. El valor por defecto es el
    /// que la API aplica a <c>claude-sonnet-5</c> cuando no se envía ninguno;
    /// no todos los modelos comparten ese valor por omisión, así que al
    /// cambiar el modelo de una ruta conviene fijar también su esfuerzo.
    /// </summary>
    public string Esfuerzo { get; set; } = "high";

    /// <summary>
    /// Modelo y esfuerzo propios de una ruta, por su clave de
    /// <see cref="RutasAnthropic"/> (p. ej. <c>Anthropic__Rutas__Ocr__Modelo</c>).
    /// Lo que una ruta no fije lo hereda de <see cref="Modelo"/> y
    /// <see cref="Esfuerzo"/>. Permite medir y elegir modelo y esfuerzo ruta a
    /// ruta sin tocar código.
    /// </summary>
    public Dictionary<string, RutaAnthropicOptions> Rutas { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Modelo y esfuerzo efectivos de una ruta.</summary>
    public (string Modelo, string Esfuerzo) Para(string ruta)
    {
        Rutas.TryGetValue(ruta, out var propia);

        return (
            string.IsNullOrWhiteSpace(propia?.Modelo) ? Modelo : propia.Modelo,
            string.IsNullOrWhiteSpace(propia?.Esfuerzo) ? Esfuerzo : propia.Esfuerzo);
    }

    /// <summary>
    /// Precio orientativo por millón de tokens de entrada/salida (USD),
    /// usado solo para el coste estimado de auditoría de
    /// <see cref="AnthropicDocumentAIProvider"/> (ver
    /// Project-Hydra-Negocio/tecnico/docs/ARQUITECTURA-IA-DOCUMENTAL.md § 4.2 — nunca un criterio de
    /// enrutado). Valores por defecto orientativos para Claude Sonnet;
    /// revisar si cambia el modelo o su tarifa.
    /// </summary>
    public decimal CostoPorMillonTokensEntrada { get; set; } = 3m;

    public decimal CostoPorMillonTokensSalida { get; set; } = 15m;
}

/// <summary>Ajustes propios de una ruta; lo que quede sin fijar se hereda de <see cref="AnthropicOptions"/>.</summary>
public class RutaAnthropicOptions
{
    public string? Modelo { get; set; }

    public string? Esfuerzo { get; set; }
}

/// <summary>Claves de las rutas de Anthropic en <see cref="AnthropicOptions.Rutas"/>.</summary>
public static class RutasAnthropic
{
    public const string Asistente = "Asistente";
    public const string RelevanciaCae = "RelevanciaCae";
    public const string VisitaCorreo = "VisitaCorreo";
    public const string GestionCorreo = "GestionCorreo";
    public const string Trabajadores = "Trabajadores";
    public const string Ocr = "Ocr";
    public const string ExtraccionEstructurada = "ExtraccionEstructurada";
}
