using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.AsistenteIa;

/// <summary>
/// Lectura por IA de metadatos (tipo, fecha de emisión, fecha de
/// vencimiento si aparece explícita, presencia de firma) de un Documento de
/// Trabajador, con un nivel de confianza general 0-100 — ver
/// VerificacionIaDocumentoService (Application). Reutiliza
/// <see cref="AnthropicOptions"/> (misma ApiKey/modelo que el resto de
/// lectura IA): sin ApiKey configurada, queda inerte igual que
/// AnthropicExtraccionTrabajadoresIaService.
/// </summary>
public class AnthropicExtraccionMetadatosDocumentoIaService(
    HttpClient httpClient,
    IOptions<AnthropicOptions> opciones,
    ILogger<AnthropicExtraccionMetadatosDocumentoIaService> logger) : IExtraccionMetadatosDocumentoIaService
{
    private const string SystemPrompt =
        """
        Eres un especialista en Coordinación de Actividades Empresariales
        (CAE) y Prevención de Riesgos Laborales (PRL) en España, actuando
        como sistema de extracción de datos, no como asistente conversacional.

        Se te proporciona un documento de un trabajador (p. ej. un
        reconocimiento médico, una entrega de EPIs, un certificado de
        formación) y el tipo de documento que se espera que sea.

        Tu única tarea es devolver un objeto JSON estricto, sin ningún texto
        adicional, sin explicaciones, sin bloques de código markdown — solo
        el objeto JSON, con exactamente estos campos:

        {"tipoDetectado": "...", "fechaEmision": "YYYY-MM-DD" o null, "fechaVencimiento": "YYYY-MM-DD" o null, "tieneFirma": true/false/null, "confianzaGeneral": 0-100, "notasValidacion": "..." o null}

        Reglas:
        - fechaEmision/fechaVencimiento: solo si aparecen explícitas en el
          documento, en formato ISO (YYYY-MM-DD). Si no puedes determinarlas
          con certeza razonable, devuelve null — no inventes ni asumas.
        - tieneFirma: true si ves una firma física, digital o electrónica en
          el documento; false si claramente no hay ninguna; null si no
          puedes determinarlo (p. ej. calidad de escaneo insuficiente).
        - confianzaGeneral: tu nivel de confianza global en esta extracción,
          0-100 — baja si el documento está borroso, incompleto, rotado o
          no coincide con el tipo esperado.
        - notasValidacion: explica brevemente cualquier duda, inconsistencia
          o motivo de baja confianza. Null si no hay nada que anotar.
        - Responde únicamente con el objeto JSON.
        """;

    public async Task<Result<MetadatosDocumentoExtraidosDto>> ExtraerAsync(
        byte[] contenidoPdf, string nombreTipoDocumento, CancellationToken cancellationToken = default)
    {
        var config = opciones.Value;

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return Result.Fallo<MetadatosDocumentoExtraidosDto>(Error.Crear(
                "VerificacionIaDocumento.NoConfigurado", "La verificación automática por IA no está disponible ahora mismo."));
        }

        var solicitud = new SolicitudAnthropic(
            config.Modelo,
            config.MaxTokensRespuesta,
            SystemPrompt,
            [
                new MensajeAnthropic("user",
                [
                    new BloqueContenido("document", new FuenteDocumento("base64", "application/pdf", Convert.ToBase64String(contenidoPdf)), null),
                    new BloqueContenido("text", null, $"Tipo de documento esperado: \"{nombreTipoDocumento}\". Extrae los metadatos siguiendo exactamente el formato indicado.")
                ])
            ]);

        using var peticion = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = JsonContent.Create(solicitud)
        };
        peticion.Headers.Add("x-api-key", config.ApiKey);
        peticion.Headers.Add("anthropic-version", "2023-06-01");

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);

            if (!respuesta.IsSuccessStatusCode)
            {
                var cuerpoError = await respuesta.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError(
                    "La API de Anthropic devolvió {StatusCode} al verificar metadatos de documento: {Cuerpo}", (int)respuesta.StatusCode, cuerpoError);

                return Result.Fallo<MetadatosDocumentoExtraidosDto>(Error.Crear(
                    "VerificacionIaDocumento.ErrorApi", "No pudimos verificar el documento automáticamente."));
            }

            var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaAnthropic>(cancellationToken);
            var texto = cuerpo?.Content.FirstOrDefault(c => c.Type == "text")?.Text;

            if (string.IsNullOrWhiteSpace(texto))
            {
                return Result.Fallo<MetadatosDocumentoExtraidosDto>(Error.Crear(
                    "VerificacionIaDocumento.RespuestaVacia", "No pudimos verificar el documento automáticamente."));
            }

            return ParsearMetadatos(texto);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al contactar la API de Anthropic para verificación de metadatos de documento.");
            return Result.Fallo<MetadatosDocumentoExtraidosDto>(Error.Crear(
                "VerificacionIaDocumento.ErrorRed", "No pudimos verificar el documento automáticamente."));
        }
    }

    /// <summary>El modelo, pese a la instrucción, a veces envuelve el JSON en un bloque de código markdown — se busca el primer '{' y el último '}' como red de seguridad, mismo criterio que AnthropicExtraccionTrabajadoresIaService.</summary>
    private Result<MetadatosDocumentoExtraidosDto> ParsearMetadatos(string texto)
    {
        var inicio = texto.IndexOf('{');
        var fin = texto.LastIndexOf('}');

        if (inicio < 0 || fin < inicio)
        {
            logger.LogWarning("La respuesta de verificación de metadatos no contenía un objeto JSON reconocible.");
            return Result.Fallo<MetadatosDocumentoExtraidosDto>(Error.Crear(
                "VerificacionIaDocumento.RespuestaInvalida", "No pudimos interpretar el resultado de la verificación automática."));
        }

        var json = texto[inicio..(fin + 1)];

        try
        {
            var extraido = JsonSerializer.Deserialize<MetadatosDocumentoJson>(json, JsonOpciones);
            if (extraido is null)
            {
                return Result.Fallo<MetadatosDocumentoExtraidosDto>(Error.Crear(
                    "VerificacionIaDocumento.RespuestaInvalida", "No pudimos interpretar el resultado de la verificación automática."));
            }

            var confianza = Math.Clamp(extraido.ConfianzaGeneral, 0, 100);

            return Result.Exito(new MetadatosDocumentoExtraidosDto(
                extraido.TipoDetectado, extraido.FechaEmision, extraido.FechaVencimiento, extraido.TieneFirma, confianza, extraido.NotasValidacion));
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "No se pudo deserializar el resultado de la verificación de metadatos de documento.");
            return Result.Fallo<MetadatosDocumentoExtraidosDto>(Error.Crear(
                "VerificacionIaDocumento.RespuestaInvalida", "No pudimos interpretar el resultado de la verificación automática."));
        }
    }

    private static readonly JsonSerializerOptions JsonOpciones = new(JsonSerializerDefaults.Web);

    private sealed record MetadatosDocumentoJson(
        string? TipoDetectado, DateOnly? FechaEmision, DateOnly? FechaVencimiento, bool? TieneFirma, int ConfianzaGeneral, string? NotasValidacion);

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
        [property: JsonPropertyName("source")] FuenteDocumento? Source,
        [property: JsonPropertyName("text")] string? Text);

    private sealed record FuenteDocumento(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("media_type")] string MediaType,
        [property: JsonPropertyName("data")] string Data);

    private sealed record RespuestaAnthropic(
        [property: JsonPropertyName("content")] IReadOnlyList<BloqueContenidoAnthropic> Content);

    private sealed record BloqueContenidoAnthropic(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text);
}
