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
/// Segundo <see cref="IDocumentAIProvider"/> real (ver
/// docs/ARQUITECTURA-IA-DOCUMENTAL.md § 4.1) — declara solo
/// <see cref="CapacidadesProveedorIa.ExtraccionEstructurada"/>, siguiendo
/// el reparto de capacidades documentado (Gemini = estructuración, Mistral
/// = OCR). Implementa también <c>ExtraerTextoAsync</c> porque Gemini lee
/// PDF/imagen de forma nativa igual que Claude, pero el router no lo llama
/// para eso mientras no declare esa capacidad — cambiarla es una línea,
/// no un adaptador nuevo. Mismo patrón "inerte por defecto" que
/// <see cref="AnthropicDocumentAIProvider"/>: sin <see cref="GeminiOptions.ApiKey"/>,
/// cada método falla con un error controlado, nunca lanza.
/// </summary>
public class GeminiDocumentAIProvider(
    HttpClient httpClient,
    IOptions<GeminiOptions> opciones,
    ILogger<GeminiDocumentAIProvider> logger) : IDocumentAIProvider
{
    public string Codigo => "gemini";

    public CapacidadesProveedorIa Capacidades => CapacidadesProveedorIa.ExtraccionEstructurada;

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

    public async Task<Result<string>> ExtraerTextoAsync(byte[] contenidoArchivo, string nombreArchivo, CancellationToken cancellationToken = default)
    {
        var config = opciones.Value;

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return Result.Fallo<string>(Error.Crear(
                "DocumentAIProvider.NoConfigurado", "La lectura automática por IA no está disponible ahora mismo."));
        }

        var mimeType = EsPdf(nombreArchivo) ? "application/pdf" : DetectarTipoImagen(contenidoArchivo);
        var solicitud = new SolicitudGemini(
            [new ContenidoGemini("user", [new ParteGemini(null, new DatosInlineGemini(mimeType, Convert.ToBase64String(contenidoArchivo)))])],
            new InstruccionSistemaGemini([new ParteGemini(SystemPromptOcr, null)]),
            new ConfiguracionGeneracionGemini(config.MaxTokensRespuesta));

        var respuesta = await EnviarAsync(solicitud, config.Modelo, "DocumentAIProvider", cancellationToken);
        if (respuesta.EsFallido)
            return Result.Fallo<string>(respuesta.Error);

        return Result.Exito(respuesta.Valor.Texto.Trim());
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

        var solicitud = new SolicitudGemini(
            [new ContenidoGemini("user", [new ParteGemini($"Tipo de documento esperado: \"{tipoEsperado}\".\n\nTexto del documento:\n{texto}", null)])],
            new InstruccionSistemaGemini([new ParteGemini(SystemPromptEstructurado, null)]),
            new ConfiguracionGeneracionGemini(config.MaxTokensRespuesta));

        var respuesta = await EnviarAsync(solicitud, config.Modelo, "DocumentAIProvider", cancellationToken);
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

    private async Task<Result<RespuestaConUso>> EnviarAsync(
        SolicitudGemini solicitud, string modelo, string prefijoError, CancellationToken cancellationToken)
    {
        using var peticion = new HttpRequestMessage(
            HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{modelo}:generateContent")
        {
            Content = JsonContent.Create(solicitud)
        };
        peticion.Headers.Add("x-goog-api-key", opciones.Value.ApiKey);

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);

            if (!respuesta.IsSuccessStatusCode)
            {
                var cuerpoError = await respuesta.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("La API de Gemini devolvió {StatusCode}: {Cuerpo}", (int)respuesta.StatusCode, cuerpoError);
                return Result.Fallo<RespuestaConUso>(Error.Crear($"{prefijoError}.ErrorApi", "No pudimos procesar el documento automáticamente."));
            }

            var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaGemini>(cancellationToken);
            var texto = cuerpo?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(texto))
            {
                return Result.Fallo<RespuestaConUso>(Error.Crear($"{prefijoError}.RespuestaVacia", "No pudimos procesar el documento automáticamente."));
            }

            return Result.Exito(new RespuestaConUso(
                texto, cuerpo?.UsageMetadata?.TokensEntrada ?? 0, cuerpo?.UsageMetadata?.TokensSalida ?? 0));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al contactar la API de Gemini.");
            return Result.Fallo<RespuestaConUso>(Error.Crear($"{prefijoError}.ErrorRed", "No pudimos procesar el documento automáticamente."));
        }
    }

    /// <summary>Igual red de seguridad que AnthropicDocumentAIProvider: el modelo a veces envuelve el JSON en un bloque de código markdown pese a la instrucción.</summary>
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

    private sealed record SolicitudGemini(
        [property: JsonPropertyName("contents")] IReadOnlyList<ContenidoGemini> Contents,
        [property: JsonPropertyName("systemInstruction")] InstruccionSistemaGemini SystemInstruction,
        [property: JsonPropertyName("generationConfig")] ConfiguracionGeneracionGemini GenerationConfig);

    private sealed record ContenidoGemini(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("parts")] IReadOnlyList<ParteGemini> Parts);

    private sealed record InstruccionSistemaGemini([property: JsonPropertyName("parts")] IReadOnlyList<ParteGemini> Parts);

    private sealed record ConfiguracionGeneracionGemini([property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens);

    private sealed record ParteGemini(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("inlineData")] DatosInlineGemini? InlineData);

    private sealed record DatosInlineGemini(
        [property: JsonPropertyName("mimeType")] string MimeType,
        [property: JsonPropertyName("data")] string Data);

    private sealed record RespuestaGemini(
        [property: JsonPropertyName("candidates")] IReadOnlyList<CandidatoGemini>? Candidates,
        [property: JsonPropertyName("usageMetadata")] UsoGemini? UsageMetadata);

    private sealed record CandidatoGemini([property: JsonPropertyName("content")] ContenidoRespuestaGemini? Content);

    private sealed record ContenidoRespuestaGemini([property: JsonPropertyName("parts")] IReadOnlyList<ParteRespuestaGemini>? Parts);

    private sealed record ParteRespuestaGemini([property: JsonPropertyName("text")] string? Text);

    private sealed record UsoGemini(
        [property: JsonPropertyName("promptTokenCount")] int TokensEntrada,
        [property: JsonPropertyName("candidatesTokenCount")] int TokensSalida);

    private sealed record RespuestaConUso(string Texto, int TokensEntrada, int TokensSalida);
}
