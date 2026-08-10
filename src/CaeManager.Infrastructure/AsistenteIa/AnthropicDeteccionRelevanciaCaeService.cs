using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.AsistenteIa;

/// <summary>
/// Clasificación por IA de texto de una conversación completa: ¿ya hay alguna gestión CAE real que
/// hacer, o es todavía solo contexto de negocio (negociación comercial de un cliente con un
/// Centro/Empresa, antes de que exista ninguna gestión)? Mismo patrón que
/// <see cref="AnthropicDeteccionVisitaCorreoService"/>/<see cref="AnthropicDeteccionGestionCorreoService"/>:
/// reutiliza <see cref="AnthropicOptions"/>, sin ApiKey configurada queda inerte.
/// </summary>
public class AnthropicDeteccionRelevanciaCaeService(
    HttpClient httpClient,
    IOptions<AnthropicOptions> opciones,
    ILogger<AnthropicDeteccionRelevanciaCaeService> logger) : IDeteccionRelevanciaCaeService
{
    private const string SystemPrompt =
        """
        Eres un sistema de clasificación de texto, no un asistente conversacional.

        Se te proporciona la transcripción de una conversación por correo entre una consultora de
        Prevención de Riesgos Laborales (España) y un cliente, ordenada cronológicamente.

        Tu única tarea es determinar si la conversación YA contiene alguna gestión CAE (Coordinación
        de Actividades Empresariales) real y concreta que un gestor deba atender — por ejemplo,
        solicitar el alta de un Centro de trabajo o de un Trabajador, pedir o entregar documentación
        obligatoria, coordinar una visita, resolver una incidencia de cumplimiento — o si, por el
        contrario, la conversación es todavía solo contexto de negocio: el cliente ha copiado al
        gestor desde el inicio de una negociación o relación comercial con un Centro o Empresa, y
        de momento no se ha pedido ni mencionado nada que requiera una acción CAE concreta.

        El cuerpo puede contener etiquetas HTML — ignóralas, interpreta solo el contenido visible.

        Devuelve exclusivamente un objeto JSON, sin texto adicional, sin explicaciones, sin bloques
        de código markdown, con este formato exacto:
        {"esAccionableCae": bool, "resumen": "una frase breve en español", "confianza": 0-100}

        Reglas:
        - esAccionableCae es true en cuanto la conversación menciona algo CAE-accionable, aunque
          sea de forma implícita o parcial (p. ej. "en cuanto arranquemos necesitaréis el alta del
          centro") — no hace falta que sea una petición formal y explícita.
        - esAccionableCae es false mientras la conversación solo hable de la negociación o relación
          comercial en sí (precios, plazos comerciales, condiciones del contrato, presentaciones),
          sin tocar todavía ningún aspecto de cumplimiento documental o coordinación de actividades.
        - resumen: una frase corta y útil para que un gestor humano entienda de un vistazo en qué
          punto está la conversación, sea o no accionable.
        - confianza: entero 0-100, tu certeza de que la clasificación es correcta.
        """;

    public async Task<Result<DeteccionRelevanciaCaeDto>> DetectarAsync(
        string cuerpoConversacion, CancellationToken cancellationToken = default)
    {
        var config = opciones.Value;

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return Result.Fallo<DeteccionRelevanciaCaeDto>(Error.Crear(
                "DeteccionRelevanciaCae.NoConfigurado", "La detección automática de relevancia CAE no está disponible ahora mismo."));
        }

        var solicitud = new SolicitudAnthropic(
            config.Modelo,
            config.MaxTokensRespuesta,
            SystemPrompt,
            [new MensajeAnthropic("user", $"Transcripción de la conversación:\n\n{cuerpoConversacion}")]);

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
                    "La API de Anthropic devolvió {StatusCode} al detectar relevancia CAE: {Cuerpo}", (int)respuesta.StatusCode, cuerpoError);

                return Result.Fallo<DeteccionRelevanciaCaeDto>(Error.Crear(
                    "DeteccionRelevanciaCae.ErrorApi", "No pudimos analizar la conversación automáticamente."));
            }

            var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaAnthropic>(cancellationToken);
            var texto = cuerpo?.Content.FirstOrDefault(c => c.Type == "text")?.Text;

            if (string.IsNullOrWhiteSpace(texto))
            {
                return Result.Fallo<DeteccionRelevanciaCaeDto>(Error.Crear(
                    "DeteccionRelevanciaCae.RespuestaVacia", "No pudimos analizar la conversación automáticamente."));
            }

            return ParsearDeteccion(texto);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al contactar la API de Anthropic para detección de relevancia CAE.");
            return Result.Fallo<DeteccionRelevanciaCaeDto>(Error.Crear(
                "DeteccionRelevanciaCae.ErrorRed", "No pudimos analizar la conversación automáticamente."));
        }
    }

    /// <summary>Mismo margen de seguridad que el resto de servicios de detección de este módulo: busca el primer '{' y el último '}' antes de deserializar.</summary>
    private Result<DeteccionRelevanciaCaeDto> ParsearDeteccion(string texto)
    {
        var inicio = texto.IndexOf('{');
        var fin = texto.LastIndexOf('}');

        if (inicio < 0 || fin < inicio)
        {
            logger.LogWarning("La respuesta de detección de relevancia CAE no contenía un objeto JSON reconocible.");
            return Result.Fallo<DeteccionRelevanciaCaeDto>(Error.Crear(
                "DeteccionRelevanciaCae.RespuestaInvalida", "No pudimos interpretar el resultado del análisis automático."));
        }

        var json = texto[inicio..(fin + 1)];

        try
        {
            var detectado = JsonSerializer.Deserialize<DeteccionRelevanciaCaeJson>(json, JsonOpciones);
            if (detectado is null)
            {
                return Result.Fallo<DeteccionRelevanciaCaeDto>(Error.Crear(
                    "DeteccionRelevanciaCae.RespuestaInvalida", "No pudimos interpretar el resultado del análisis automático."));
            }

            var confianza = Math.Clamp(detectado.Confianza, 0, 100);
            return Result.Exito(new DeteccionRelevanciaCaeDto(detectado.EsAccionableCae, detectado.Resumen, confianza));
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "No se pudo deserializar el resultado de la detección de relevancia CAE.");
            return Result.Fallo<DeteccionRelevanciaCaeDto>(Error.Crear(
                "DeteccionRelevanciaCae.RespuestaInvalida", "No pudimos interpretar el resultado del análisis automático."));
        }
    }

    private static readonly JsonSerializerOptions JsonOpciones = new(JsonSerializerDefaults.Web);

    private sealed record DeteccionRelevanciaCaeJson(bool EsAccionableCae, string? Resumen, int Confianza);

    private sealed record SolicitudAnthropic(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("system")] string System,
        [property: JsonPropertyName("messages")] IReadOnlyList<MensajeAnthropic> Messages);

    private sealed record MensajeAnthropic(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record RespuestaAnthropic(
        [property: JsonPropertyName("content")] IReadOnlyList<BloqueContenidoAnthropic> Content);

    private sealed record BloqueContenidoAnthropic(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text);
}
