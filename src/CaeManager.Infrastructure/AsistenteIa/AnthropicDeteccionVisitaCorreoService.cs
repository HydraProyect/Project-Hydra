using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.AsistenteIa;

/// <summary>
/// Clasificación por IA de texto de correos entrantes: ¿pide este mensaje
/// agendar una visita? Reutiliza <see cref="AnthropicOptions"/> (misma
/// ApiKey/modelo que el resto de servicios de Anthropic); sin ApiKey
/// configurada queda inerte — la ingesta del correo sigue funcionando, solo
/// sin sugerencia de visita. El cuerpo se envía tal cual (puede traer HTML,
/// el propio prompt indica al modelo que lo ignore) — no hace falta un
/// parser HTML dedicado para esto, a diferencia de mostrar el cuerpo en
/// pantalla.
/// </summary>
public class AnthropicDeteccionVisitaCorreoService(
    HttpClient httpClient,
    IOptions<AnthropicOptions> opciones,
    ILogger<AnthropicDeteccionVisitaCorreoService> logger) : IDeteccionVisitaCorreoService
{
    // Tope defensivo: un hilo de correo con mucho histórico citado puede
    // crecer bastante, y no aporta nada extra a la clasificación pasado
    // cierto punto — igual que el resto de topes "no es un límite de la API,
    // es prudencia de este job de fondo" del módulo de Comunicaciones.
    private const int LongitudMaximaCuerpo = 8000;

    private const string SystemPrompt =
        """
        Eres un sistema de clasificación y extracción de datos, no un asistente conversacional.

        Se te proporciona el cuerpo de un correo recibido por una consultora
        de Prevención de Riesgos Laborales (España) y una lista de Centros
        de trabajo del cliente que envía el correo.

        Tu única tarea es determinar si el correo es una solicitud de
        agendar la entrada de uno o más trabajadores a un Centro (una
        "visita") y, si lo es, extraer los datos disponibles.

        El cuerpo puede contener etiquetas HTML — ignóralas, interpreta solo
        el contenido visible del mensaje.

        Devuelve exclusivamente un objeto JSON, sin texto adicional, sin
        explicaciones, sin bloques de código markdown, con este formato
        exacto:
        {"esSolicitudVisita": bool, "centroId": "guid-de-la-lista-o-null", "fechaInicio": "YYYY-MM-DD-o-null", "fechaFin": "YYYY-MM-DD-o-null", "resumen": "una frase breve en español"}

        Reglas:
        - esSolicitudVisita es true solo si el correo pide explícita o
          implícitamente agendar la entrada de trabajadores a un centro de
          trabajo en una fecha o rango de fechas.
        - centroId debe ser exactamente uno de los Id de la lista de centros
          proporcionada, o null si el correo no deja claro a cuál se refiere.
        - Interpreta fechas relativas ("el próximo lunes", "la semana que
          viene") usando la fecha de referencia proporcionada. Si no puedes
          determinar una fecha con confianza razonable, devuelve null.
        - resumen: una frase corta y útil para que un gestor humano decida
          rápido si merece revisarlo (menciona trabajadores o subcontrata si
          aparecen). Obligatorio incluso si esSolicitudVisita es false,
          explicando brevemente por qué no lo es.
        - Si esSolicitudVisita es false, centroId, fechaInicio y fechaFin
          deben ser null.
        """;

    public async Task<Result<DeteccionVisitaCorreoDto>> DetectarAsync(
        string cuerpoMensaje, IReadOnlyList<CentroCandidatoVisitaDto> centrosDisponibles, DateOnly fechaReferencia,
        CancellationToken cancellationToken = default)
    {
        var config = opciones.Value;

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return Result.Fallo<DeteccionVisitaCorreoDto>(Error.Crear(
                "DeteccionVisitaCorreo.NoConfigurado", "La detección automática de visitas no está disponible ahora mismo."));
        }

        var cuerpoRecortado = cuerpoMensaje.Length > LongitudMaximaCuerpo ? cuerpoMensaje[..LongitudMaximaCuerpo] : cuerpoMensaje;
        var centrosJson = JsonSerializer.Serialize(centrosDisponibles.Select(c => new { id = c.Id, nombre = c.Nombre }));

        var textoUsuario =
            $"""
            Fecha de referencia (hoy): {fechaReferencia:yyyy-MM-dd}

            Centros disponibles (usa exactamente uno de estos Id, o null):
            {centrosJson}

            Cuerpo del correo:
            {cuerpoRecortado}
            """;

        var solicitud = new SolicitudAnthropic(
            config.Modelo,
            config.MaxTokensRespuesta,
            SystemPrompt,
            [new MensajeAnthropic("user", textoUsuario)]);

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
                    "La API de Anthropic devolvió {StatusCode} al detectar visita en correo: {Cuerpo}", (int)respuesta.StatusCode, cuerpoError);

                return Result.Fallo<DeteccionVisitaCorreoDto>(Error.Crear(
                    "DeteccionVisitaCorreo.ErrorApi", "No pudimos analizar el correo automáticamente."));
            }

            var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaAnthropic>(cancellationToken);
            var texto = cuerpo?.Content.FirstOrDefault(c => c.Type == "text")?.Text;

            if (string.IsNullOrWhiteSpace(texto))
            {
                return Result.Fallo<DeteccionVisitaCorreoDto>(Error.Crear(
                    "DeteccionVisitaCorreo.RespuestaVacia", "No pudimos analizar el correo automáticamente."));
            }

            return ParsearDeteccion(texto, centrosDisponibles);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al contactar la API de Anthropic para detección de visita en correo.");
            return Result.Fallo<DeteccionVisitaCorreoDto>(Error.Crear(
                "DeteccionVisitaCorreo.ErrorRed", "No pudimos analizar el correo automáticamente."));
        }
    }

    /// <summary>
    /// Mismo margen de seguridad que AnthropicExtraccionTrabajadoresIaService:
    /// busca el primer '{' y el último '}' antes de deserializar, por si el
    /// modelo envuelve el JSON en un bloque de código pese a la instrucción.
    /// centroId se descarta (se fuerza a null) si no coincide con ninguno de
    /// los candidatos pasados — nunca se confía en un Id fuera de esa lista.
    /// </summary>
    private Result<DeteccionVisitaCorreoDto> ParsearDeteccion(string texto, IReadOnlyList<CentroCandidatoVisitaDto> centrosDisponibles)
    {
        var inicio = texto.IndexOf('{');
        var fin = texto.LastIndexOf('}');

        if (inicio < 0 || fin < inicio)
        {
            logger.LogWarning("La respuesta de detección de visita en correo no contenía un objeto JSON reconocible.");
            return Result.Fallo<DeteccionVisitaCorreoDto>(Error.Crear(
                "DeteccionVisitaCorreo.RespuestaInvalida", "No pudimos interpretar el resultado del análisis automático."));
        }

        var json = texto[inicio..(fin + 1)];

        try
        {
            var detectado = JsonSerializer.Deserialize<DeteccionVisitaJson>(json, JsonOpciones);
            if (detectado is null)
            {
                return Result.Fallo<DeteccionVisitaCorreoDto>(Error.Crear(
                    "DeteccionVisitaCorreo.RespuestaInvalida", "No pudimos interpretar el resultado del análisis automático."));
            }

            var centroId = detectado.CentroId is not null
                && Guid.TryParse(detectado.CentroId, out var idParseado)
                && centrosDisponibles.Any(c => c.Id == idParseado)
                    ? idParseado
                    : (Guid?)null;

            var fechaInicio = DateOnly.TryParse(detectado.FechaInicio, out var inicioParseado) ? inicioParseado : (DateOnly?)null;
            var fechaFin = DateOnly.TryParse(detectado.FechaFin, out var finParseado) ? finParseado : (DateOnly?)null;

            if (fechaInicio is not null && fechaFin is not null && fechaFin < fechaInicio)
                fechaFin = null; // rango inconsistente del modelo — mejor no sugerir fecha de fin que una incoherente.

            return Result.Exito(new DeteccionVisitaCorreoDto(detectado.EsSolicitudVisita, centroId, fechaInicio, fechaFin, detectado.Resumen));
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "No se pudo deserializar el resultado de la detección de visita en correo.");
            return Result.Fallo<DeteccionVisitaCorreoDto>(Error.Crear(
                "DeteccionVisitaCorreo.RespuestaInvalida", "No pudimos interpretar el resultado del análisis automático."));
        }
    }

    private static readonly JsonSerializerOptions JsonOpciones = new(JsonSerializerDefaults.Web);

    private sealed record DeteccionVisitaJson(
        bool EsSolicitudVisita, string? CentroId, string? FechaInicio, string? FechaFin, string? Resumen);

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
