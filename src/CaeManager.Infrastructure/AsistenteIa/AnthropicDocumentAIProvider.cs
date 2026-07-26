using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.DocumentosIa;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.AsistenteIa;

/// <summary>
/// Primer <see cref="IDocumentAIProvider"/> real, sobre Anthropic — para
/// pruebas puntuales mientras no haya claves de Gemini/Mistral (ver
/// docs/ARQUITECTURA-IA-DOCUMENTAL.md § 4.1). A diferencia del reparto real
/// (Mistral = solo OCR, Gemini = solo estructuración), Claude sabe hacer
/// ambas cosas — de ahí que declare las dos capacidades a la vez. Mismo
/// patrón "inerte por defecto" que el resto de servicios de Anthropic:
/// sin <see cref="AnthropicOptions.ApiKey"/>, cada método falla con un
/// error controlado, nunca lanza.
/// </summary>
public class AnthropicDocumentAIProvider(
    HttpClient httpClient,
    IOptions<AnthropicOptions> opciones,
    ILogger<AnthropicDocumentAIProvider> logger) : IDocumentAIProvider
{
    public string Codigo => "anthropic";

    public CapacidadesProveedorIa Capacidades => CapacidadesProveedorIa.OcrImagenAEscaneado | CapacidadesProveedorIa.ExtraccionEstructurada;

    private const string SystemPromptOcr =
        """
        Eres un sistema de OCR, no un asistente conversacional. Transcribe
        exactamente el texto que aparece en el documento o imagen, tal cual,
        sin traducir, resumir ni corregir errores. Responde únicamente con
        el texto transcrito, sin explicaciones ni comentarios. Si no
        contiene texto legible, responde con una cadena vacía.
        """;

    private const string SystemPromptEstructurado =
        """
        Eres un especialista en Coordinación de Actividades Empresariales
        (CAE) y Prevención de Riesgos Laborales (PRL) en España, actuando
        como sistema de extracción de datos, no como asistente conversacional.

        Se te proporciona el texto de un documento (ya extraído, digital o
        vía OCR) y el tipo de documento que se espera que sea.

        Tu única tarea es devolver un objeto JSON estricto, sin ningún texto
        adicional, sin explicaciones, sin bloques de código markdown — solo
        el objeto JSON, con exactamente estos campos:

        {"tipoDetectado": "...", "campos": {"fechaEmision": "YYYY-MM-DD" o ausente, "fechaVencimiento": "YYYY-MM-DD" o ausente, "tieneFirma": "true"/"false" o ausente, "nombreDeCampo": "valor", ...}, "confianzaGeneral": 0-100, "notasValidacion": "..." o null}

        Reglas:
        - "fechaEmision"/"fechaVencimiento" (si aparecen explícitas, formato
          ISO YYYY-MM-DD) y "tieneFirma" ("true" si detectas una firma
          física/digital/electrónica, "false" si claramente no hay ninguna)
          son campos comunes a casi cualquier documento CAE — inclúyelos
          cuando puedas determinarlos.
        - Además de esos tres, "campos" contiene todos los demás datos
          relevantes que puedas extraer con certeza razonable (importes,
          números de referencia, partes implicadas, coberturas, etc.), como
          pares clave/valor de texto — usa nombres de campo descriptivos en
          minúscula.
        - No inventes información. Si un dato no puede extraerse con
          certeza, no lo incluyas en "campos" y explica el motivo en
          "notasValidacion".
        - confianzaGeneral: tu nivel de confianza global en esta extracción, 0-100.
        - Responde únicamente con el objeto JSON.
        """;

    public async Task<Result<TextoExtraccionDto>> ExtraerTextoAsync(byte[] contenidoArchivo, string nombreArchivo, CancellationToken cancellationToken = default)
    {
        var config = opciones.Value;

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return Result.Fallo<TextoExtraccionDto>(Error.Crear(
                "DocumentAIProvider.NoConfigurado", "La lectura automática por IA no está disponible ahora mismo."));
        }

        var bloqueArchivo = EsPdf(nombreArchivo)
            ? new BloqueContenido("document", new FuenteArchivo("base64", "application/pdf", Convert.ToBase64String(contenidoArchivo)), null)
            : new BloqueContenido("image", new FuenteArchivo("base64", DetectarTipoImagen(contenidoArchivo), Convert.ToBase64String(contenidoArchivo)), null);

        var solicitud = new SolicitudAnthropic(
            config.Modelo,
            config.MaxTokensRespuesta,
            SystemPromptOcr,
            [
                new MensajeAnthropic("user",
                [
                    bloqueArchivo,
                    new BloqueContenido("text", null, "Transcribe el texto de este documento.")
                ])
            ]);

        var respuesta = await EnviarAsync(solicitud, "DocumentAIProvider", cancellationToken);
        if (respuesta.EsFallido)
            return Result.Fallo<TextoExtraccionDto>(respuesta.Error);

        var coste = CalcularCoste(respuesta.Valor.TokensEntrada, respuesta.Valor.TokensSalida);
        return Result.Exito(new TextoExtraccionDto(respuesta.Valor.Texto.Trim(), coste));
    }

    private static bool EsPdf(string nombreArchivo) => nombreArchivo.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    public async Task<Result<ExtraccionEstructuradaDto>> ExtraerEstructuradoAsync(
        string texto, string tipoEsperado, CancellationToken cancellationToken = default)
    {
        var config = opciones.Value;

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear(
                "DocumentAIProvider.NoConfigurado", "La extracción automática por IA no está disponible ahora mismo."));
        }

        var solicitud = new SolicitudAnthropic(
            config.Modelo,
            config.MaxTokensRespuesta,
            SystemPromptEstructurado,
            [
                new MensajeAnthropic("user",
                [
                    new BloqueContenido("text", null, $"Tipo de documento esperado: \"{tipoEsperado}\".\n\nTexto del documento:\n{texto}")
                ])
            ]);

        var respuesta = await EnviarAsync(solicitud, "DocumentAIProvider", cancellationToken);
        if (respuesta.EsFallido)
            return Result.Fallo<ExtraccionEstructuradaDto>(respuesta.Error);

        var costeEstimado = CalcularCoste(respuesta.Valor.TokensEntrada, respuesta.Valor.TokensSalida);
        return ParsearEstructurado(respuesta.Valor.Texto, costeEstimado);
    }

    /// <summary>Coste orientativo, solo para auditoría (ver docs/ARQUITECTURA-IA-DOCUMENTAL.md § 4.2) — nunca se usa para decidir enrutado.</summary>
    private decimal CalcularCoste(int tokensEntrada, int tokensSalida)
    {
        var config = opciones.Value;
        return tokensEntrada / 1_000_000m * config.CostoPorMillonTokensEntrada
            + tokensSalida / 1_000_000m * config.CostoPorMillonTokensSalida;
    }

    private async Task<Result<RespuestaConUso>> EnviarAsync(SolicitudAnthropic solicitud, string prefijoError, CancellationToken cancellationToken)
    {
        using var peticion = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = JsonContent.Create(solicitud)
        };
        peticion.Headers.Add("x-api-key", opciones.Value.ApiKey);
        peticion.Headers.Add("anthropic-version", "2023-06-01");

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);

            if (!respuesta.IsSuccessStatusCode)
            {
                var cuerpoError = await respuesta.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("La API de Anthropic devolvió {StatusCode}: {Cuerpo}", (int)respuesta.StatusCode, cuerpoError);
                return Result.Fallo<RespuestaConUso>(Error.Crear($"{prefijoError}.ErrorApi", "No pudimos procesar el documento automáticamente."));
            }

            var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaAnthropic>(cancellationToken);
            var texto = cuerpo?.Content.FirstOrDefault(c => c.Type == "text")?.Text;

            if (string.IsNullOrWhiteSpace(texto))
            {
                return Result.Fallo<RespuestaConUso>(Error.Crear($"{prefijoError}.RespuestaVacia", "No pudimos procesar el documento automáticamente."));
            }

            return Result.Exito(new RespuestaConUso(texto, cuerpo?.Usage?.TokensEntrada ?? 0, cuerpo?.Usage?.TokensSalida ?? 0));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al contactar la API de Anthropic.");
            return Result.Fallo<RespuestaConUso>(Error.Crear($"{prefijoError}.ErrorRed", "No pudimos procesar el documento automáticamente."));
        }
    }

    /// <summary>Igual red de seguridad que el resto de servicios de Anthropic: el modelo a veces envuelve el JSON en un bloque de código markdown pese a la instrucción.</summary>
    private Result<ExtraccionEstructuradaDto> ParsearEstructurado(string texto, decimal costeEstimado)
    {
        var inicio = texto.IndexOf('{');
        var fin = texto.LastIndexOf('}');

        if (inicio < 0 || fin < inicio)
        {
            logger.LogWarning("La respuesta de extracción estructurada no contenía un objeto JSON reconocible.");
            return Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear(
                "DocumentAIProvider.RespuestaInvalida", "No pudimos interpretar el resultado de la extracción automática."));
        }

        var json = texto[inicio..(fin + 1)];

        try
        {
            var extraido = JsonSerializer.Deserialize<ExtraccionEstructuradaJson>(json, JsonOpciones);
            if (extraido is null)
            {
                return Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear(
                    "DocumentAIProvider.RespuestaInvalida", "No pudimos interpretar el resultado de la extracción automática."));
            }

            var confianza = Math.Clamp(extraido.ConfianzaGeneral, 0, 100);
            var campos = (extraido.Campos ?? new Dictionary<string, string?>()) as IReadOnlyDictionary<string, string?>;

            return Result.Exito(new ExtraccionEstructuradaDto(extraido.TipoDetectado, campos, confianza, extraido.NotasValidacion, costeEstimado));
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "No se pudo deserializar el resultado de la extracción estructurada.");
            return Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear(
                "DocumentAIProvider.RespuestaInvalida", "No pudimos interpretar el resultado de la extracción automática."));
        }
    }

    private static string DetectarTipoImagen(byte[] contenido)
    {
        if (contenido.Length >= 8 && contenido[0] == 0x89 && contenido[1] == 0x50 && contenido[2] == 0x4E && contenido[3] == 0x47)
            return "image/png";
        if (contenido.Length >= 3 && contenido[0] == 0xFF && contenido[1] == 0xD8)
            return "image/jpeg";
        return "image/jpeg";
    }

    private static readonly JsonSerializerOptions JsonOpciones = new(JsonSerializerDefaults.Web);

    private sealed record ExtraccionEstructuradaJson(
        string? TipoDetectado, Dictionary<string, string?>? Campos, int ConfianzaGeneral, string? NotasValidacion);

    private sealed record SolicitudAnthropic(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("system")] string System,
        [property: JsonPropertyName("messages")] IReadOnlyList<MensajeAnthropic> Messages);

    private sealed record MensajeAnthropic(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] IReadOnlyList<BloqueContenido> Content);

    private sealed record BloqueContenido(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("source")] FuenteArchivo? Source,
        [property: JsonPropertyName("text")] string? Text);

    private sealed record FuenteArchivo(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("media_type")] string MediaType,
        [property: JsonPropertyName("data")] string Data);

    private sealed record RespuestaAnthropic(
        [property: JsonPropertyName("content")] IReadOnlyList<BloqueContenidoAnthropic> Content,
        [property: JsonPropertyName("usage")] UsoAnthropic? Usage);

    private sealed record BloqueContenidoAnthropic(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text);

    private sealed record UsoAnthropic(
        [property: JsonPropertyName("input_tokens")] int TokensEntrada,
        [property: JsonPropertyName("output_tokens")] int TokensSalida);

    private sealed record RespuestaConUso(string Texto, int TokensEntrada, int TokensSalida);
}
