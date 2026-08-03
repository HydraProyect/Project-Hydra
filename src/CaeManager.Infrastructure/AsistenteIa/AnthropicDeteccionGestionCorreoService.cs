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
/// actualizar/renovar un documento (p. ej. EPI) de un Trabajador? Mismo
/// patrón que <see cref="AnthropicDeteccionVisitaCorreoService"/> — sin
/// ApiKey configurada queda inerte, la ingesta del correo sigue funcionando,
/// solo sin sugerencia de gestión.
/// </summary>
public class AnthropicDeteccionGestionCorreoService(
    HttpClient httpClient,
    IOptions<AnthropicOptions> opciones,
    ILogger<AnthropicDeteccionGestionCorreoService> logger) : IDeteccionGestionCorreoService
{
    private const int LongitudMaximaCuerpo = 8000;

    private const string SystemPrompt =
        """
        Eres un sistema de clasificación y extracción de datos, no un asistente conversacional.

        Se te proporciona el cuerpo de un correo recibido por una consultora
        de Prevención de Riesgos Laborales (España), una lista de
        Trabajadores candidatos (con su DNI si está disponible) y una lista
        de tipos de documento posibles (p. ej. EPI, Formación, Apto médico).

        Tu única tarea es determinar si el correo pide explícita o
        implícitamente actualizar, renovar o entregar un documento de uno de
        esos tipos para uno de esos Trabajadores, y si lo es, extraer a cuál
        Trabajador y a cuál tipo de documento se refiere.

        El cuerpo puede contener etiquetas HTML — ignóralas, interpreta solo
        el contenido visible del mensaje.

        Devuelve exclusivamente un objeto JSON, sin texto adicional, sin
        explicaciones, sin bloques de código markdown, con este formato
        exacto:
        {"esActualizacionDocumento": bool, "trabajadorId": "guid-de-la-lista-o-null", "tipoDocumentoId": "guid-de-la-lista-o-null", "resumen": "una frase breve en español"}

        Reglas:
        - esActualizacionDocumento es true solo si el correo pide o informa
          explícita o implícitamente sobre la actualización/renovación/
          entrega de un documento de un Trabajador concreto.
        - trabajadorId y tipoDocumentoId deben ser exactamente uno de los Id
          de las listas proporcionadas, o null si el correo no deja claro a
          cuál se refiere. Si no puedes identificar AMBOS con confianza
          razonable, deja esActualizacionDocumento en true igualmente pero
          pon a null el que no puedas resolver — el Gestor humano decide qué
          hacer con una sugerencia incompleta.
        - resumen: una frase corta y útil para que un gestor humano decida
          rápido si merece revisarlo (menciona el trabajador y el tipo de
          documento si aparecen). Obligatorio incluso si
          esActualizacionDocumento es false, explicando brevemente por qué
          no lo es.
        - Si esActualizacionDocumento es false, trabajadorId y
          tipoDocumentoId deben ser null.
        """;

    public async Task<Result<DeteccionGestionCorreoDto>> DetectarAsync(
        string cuerpoMensaje,
        IReadOnlyList<TrabajadorCandidatoGestionDto> trabajadoresDisponibles,
        IReadOnlyList<TipoDocumentoCandidatoGestionDto> tiposDocumentoDisponibles,
        CancellationToken cancellationToken = default)
    {
        var config = opciones.Value;

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return Result.Fallo<DeteccionGestionCorreoDto>(Error.Crear(
                "DeteccionGestionCorreo.NoConfigurado", "La detección automática de gestiones no está disponible ahora mismo."));
        }

        var cuerpoRecortado = cuerpoMensaje.Length > LongitudMaximaCuerpo ? cuerpoMensaje[..LongitudMaximaCuerpo] : cuerpoMensaje;
        var trabajadoresJson = JsonSerializer.Serialize(
            trabajadoresDisponibles.Select(t => new { id = t.Id, nombre = t.NombreCompleto, dni = t.Dni }));
        var tiposDocumentoJson = JsonSerializer.Serialize(
            tiposDocumentoDisponibles.Select(t => new { id = t.Id, nombre = t.Nombre }));

        var textoUsuario =
            $"""
            Trabajadores candidatos (usa exactamente uno de estos Id, o null):
            {trabajadoresJson}

            Tipos de documento disponibles (usa exactamente uno de estos Id, o null):
            {tiposDocumentoJson}

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
                    "La API de Anthropic devolvió {StatusCode} al detectar gestión en correo: {Cuerpo}", (int)respuesta.StatusCode, cuerpoError);

                return Result.Fallo<DeteccionGestionCorreoDto>(Error.Crear(
                    "DeteccionGestionCorreo.ErrorApi", "No pudimos analizar el correo automáticamente."));
            }

            var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaAnthropic>(cancellationToken);
            var texto = cuerpo?.Content.FirstOrDefault(c => c.Type == "text")?.Text;

            if (string.IsNullOrWhiteSpace(texto))
            {
                return Result.Fallo<DeteccionGestionCorreoDto>(Error.Crear(
                    "DeteccionGestionCorreo.RespuestaVacia", "No pudimos analizar el correo automáticamente."));
            }

            return ParsearDeteccion(texto, trabajadoresDisponibles, tiposDocumentoDisponibles);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al contactar la API de Anthropic para detección de gestión en correo.");
            return Result.Fallo<DeteccionGestionCorreoDto>(Error.Crear(
                "DeteccionGestionCorreo.ErrorRed", "No pudimos analizar el correo automáticamente."));
        }
    }

    /// <summary>
    /// Mismo margen de seguridad que AnthropicDeteccionVisitaCorreoService:
    /// busca el primer '{' y el último '}' antes de deserializar. trabajadorId
    /// y tipoDocumentoId se descartan (se fuerzan a null) si no coinciden con
    /// ninguno de los candidatos pasados — nunca se confía en un Id fuera de
    /// esas listas.
    /// </summary>
    private Result<DeteccionGestionCorreoDto> ParsearDeteccion(
        string texto,
        IReadOnlyList<TrabajadorCandidatoGestionDto> trabajadoresDisponibles,
        IReadOnlyList<TipoDocumentoCandidatoGestionDto> tiposDocumentoDisponibles)
    {
        var inicio = texto.IndexOf('{');
        var fin = texto.LastIndexOf('}');

        if (inicio < 0 || fin < inicio)
        {
            logger.LogWarning("La respuesta de detección de gestión en correo no contenía un objeto JSON reconocible.");
            return Result.Fallo<DeteccionGestionCorreoDto>(Error.Crear(
                "DeteccionGestionCorreo.RespuestaInvalida", "No pudimos interpretar el resultado del análisis automático."));
        }

        var json = texto[inicio..(fin + 1)];

        try
        {
            var detectado = JsonSerializer.Deserialize<DeteccionGestionJson>(json, JsonOpciones);
            if (detectado is null)
            {
                return Result.Fallo<DeteccionGestionCorreoDto>(Error.Crear(
                    "DeteccionGestionCorreo.RespuestaInvalida", "No pudimos interpretar el resultado del análisis automático."));
            }

            var trabajadorId = detectado.TrabajadorId is not null
                && Guid.TryParse(detectado.TrabajadorId, out var trabajadorIdParseado)
                && trabajadoresDisponibles.Any(t => t.Id == trabajadorIdParseado)
                    ? trabajadorIdParseado
                    : (Guid?)null;

            var tipoDocumentoId = detectado.TipoDocumentoId is not null
                && Guid.TryParse(detectado.TipoDocumentoId, out var tipoDocumentoIdParseado)
                && tiposDocumentoDisponibles.Any(t => t.Id == tipoDocumentoIdParseado)
                    ? tipoDocumentoIdParseado
                    : (Guid?)null;

            return Result.Exito(new DeteccionGestionCorreoDto(
                detectado.EsActualizacionDocumento, trabajadorId, tipoDocumentoId, detectado.Resumen));
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "No se pudo deserializar el resultado de la detección de gestión en correo.");
            return Result.Fallo<DeteccionGestionCorreoDto>(Error.Crear(
                "DeteccionGestionCorreo.RespuestaInvalida", "No pudimos interpretar el resultado del análisis automático."));
        }
    }

    private static readonly JsonSerializerOptions JsonOpciones = new(JsonSerializerDefaults.Web);

    private sealed record DeteccionGestionJson(
        bool EsActualizacionDocumento, string? TrabajadorId, string? TipoDocumentoId, string? Resumen);

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
