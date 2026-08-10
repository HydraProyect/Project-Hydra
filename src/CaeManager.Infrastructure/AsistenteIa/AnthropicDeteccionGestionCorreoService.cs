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
        esos tipos para uno o varios de esos Trabajadores, y si lo es,
        extraer CADA Trabajador y CADA tipo de documento a los que se
        refiere. Un correo puede ser una notificación en bloque que liste el
        estado de varios Trabajadores a la vez (p. ej. una plataforma
        externa avisando de varios documentos pendientes de distinta gente
        en el mismo aviso) — en ese caso devuelve un ítem por cada uno, no
        solo el primero.

        El cuerpo puede contener etiquetas HTML — ignóralas, interpreta solo
        el contenido visible del mensaje.

        Devuelve exclusivamente un objeto JSON, sin texto adicional, sin
        explicaciones, sin bloques de código markdown, con este formato
        exacto:
        {"esActualizacionDocumento": bool, "resumen": "una frase breve en español", "confianza": 0-100, "items": [{"trabajadorId": "guid-de-la-lista-o-null", "tipoDocumentoId": "guid-de-la-lista-o-null", "confianzaTrabajador": 0-100, "confianzaTipoDocumento": 0-100}]}

        Reglas:
        - esActualizacionDocumento es true solo si el correo pide o informa
          explícita o implícitamente sobre la actualización/renovación/
          entrega de un documento de uno o varios Trabajadores concretos.
        - items es la lista de ítems detectados, uno por cada combinación
          Trabajador/tipo de documento distinta que menciona el correo. Si
          esActualizacionDocumento es true pero no puedes distinguir varios
          ítems (un único trabajador/documento, o no puedes separarlos),
          devuelve un único ítem. Si esActualizacionDocumento es false,
          items debe ser una lista vacía.
        - trabajadorId y tipoDocumentoId de cada ítem deben ser exactamente
          uno de los Id de las listas proporcionadas, o null si el correo no
          deja claro a cuál se refiere ese ítem concreto. Si no puedes
          identificar AMBOS con confianza razonable, incluye el ítem
          igualmente pero pon a null el que no puedas resolver — el Gestor
          humano decide qué hacer con un ítem incompleto.
        - resumen: una frase corta y útil para que un gestor humano decida
          rápido si merece revisarlo (menciona cuántos trabajadores/
          documentos aparecen si son varios). Obligatorio incluso si
          esActualizacionDocumento es false, explicando brevemente por qué
          no lo es.
        - confianza: entero 0-100, tu propia certeza global de que
          esActualizacionDocumento es correcto — sobre el mensaje completo,
          no sobre un ítem en particular.
        - confianzaTrabajador de un ítem: entero 0-100, tu certeza
          específica de que ese trabajadorId es el trabajador correcto para
          ese ítem. 0 si trabajadorId es null.
        - confianzaTipoDocumento de un ítem: entero 0-100, tu certeza
          específica de que ese tipoDocumentoId es el tipo correcto para ese
          ítem. 0 si tipoDocumentoId es null.
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
    /// y tipoDocumentoId de cada ítem se descartan (se fuerzan a null) si no
    /// coinciden con ninguno de los candidatos pasados — nunca se confía en
    /// un Id fuera de esas listas.
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

            var items = (detectado.Items ?? [])
                .Select(item =>
                {
                    var trabajadorId = item.TrabajadorId is not null
                        && Guid.TryParse(item.TrabajadorId, out var trabajadorIdParseado)
                        && trabajadoresDisponibles.Any(t => t.Id == trabajadorIdParseado)
                            ? trabajadorIdParseado
                            : (Guid?)null;

                    var tipoDocumentoId = item.TipoDocumentoId is not null
                        && Guid.TryParse(item.TipoDocumentoId, out var tipoDocumentoIdParseado)
                        && tiposDocumentoDisponibles.Any(t => t.Id == tipoDocumentoIdParseado)
                            ? tipoDocumentoIdParseado
                            : (Guid?)null;

                    return new ItemDeteccionGestionDto(
                        trabajadorId, tipoDocumentoId,
                        Math.Clamp(item.ConfianzaTrabajador, 0, 100), Math.Clamp(item.ConfianzaTipoDocumento, 0, 100));
                })
                .ToList();

            var confianza = Math.Clamp(detectado.Confianza, 0, 100);

            return Result.Exito(new DeteccionGestionCorreoDto(detectado.EsActualizacionDocumento, detectado.Resumen, confianza, items));
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
        bool EsActualizacionDocumento, string? Resumen, int Confianza, IReadOnlyList<ItemDeteccionGestionJson>? Items);

    private sealed record ItemDeteccionGestionJson(
        string? TrabajadorId, string? TipoDocumentoId, int ConfianzaTrabajador, int ConfianzaTipoDocumento);

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
