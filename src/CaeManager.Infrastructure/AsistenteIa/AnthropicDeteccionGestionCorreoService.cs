using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CaeManager.Application.AsistenteIa.Preparacion;
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
public partial class AnthropicDeteccionGestionCorreoService(
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
        Trabajadores candidatos (con el marcador de su documento de identidad
        cuando ese documento aparece en el correo) y una lista de tipos de
        documento posibles (p. ej. EPI, Formación, Apto médico).

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

        Algunas plataformas externas no dan detalle por trabajador/documento:
        solo informan un resumen agregado ("3 pendientes, 1 vencido, 0
        rechazados"). Cuando el correo es de ese tipo (un resumen numérico
        sin decir de quién ni de qué documento concreto), no intentes
        inventar items — devuelve items como lista vacía y rellena
        resumenAgregado con esas tres cifras en su lugar.

        Devuelve exclusivamente un objeto JSON, sin texto adicional, sin
        explicaciones, sin bloques de código markdown, con este formato
        exacto:
        {"esActualizacionDocumento": bool, "resumen": "una frase breve en español", "confianza": 0-100, "items": [{"trabajadorId": "guid-de-la-lista-o-null", "tipoDocumentoId": "guid-de-la-lista-o-null", "confianzaTrabajador": 0-100, "confianzaTipoDocumento": 0-100}], "resumenAgregado": {"pendientes": 0, "vencidos": 0, "rechazados": 0} | null}

        Reglas:
        - esActualizacionDocumento es true solo si el correo pide o informa
          explícita o implícitamente sobre la actualización/renovación/
          entrega de un documento de uno o varios Trabajadores concretos, O
          es un resumen agregado de una plataforma sin detalle por persona.
        - items es la lista de ítems detectados, uno por cada combinación
          Trabajador/tipo de documento distinta que menciona el correo. Si
          esActualizacionDocumento es true pero no puedes distinguir varios
          ítems (un único trabajador/documento, o no puedes separarlos),
          devuelve un único ítem. Si esActualizacionDocumento es false, o el
          correo es un resumen agregado sin detalle, items debe ser una
          lista vacía.
        - resumenAgregado: null salvo que el correo sea justo ese caso — un
          resumen numérico de una plataforma sin trabajador ni documento
          identificable. Nunca se rellena a la vez que items tiene
          elementos — es uno u otro, no ambos. pendientes/vencidos/rechazados
          son los tres contadores tal como los da el correo (0 si el correo
          no menciona esa categoría explícitamente pero sí las otras dos).
        - trabajadorId y tipoDocumentoId de cada ítem deben ser exactamente
          uno de los Id de las listas proporcionadas, o null si el correo no
          deja claro a cuál se refiere ese ítem concreto. Si no puedes
          identificar AMBOS con confianza razonable, incluye el ítem
          igualmente pero pon a null el que no puedas resolver — el Gestor
          humano decide qué hacer con un ítem incompleto.
        - resumen: una frase corta y útil para que un gestor humano decida
          rápido si merece revisarlo (menciona cuántos trabajadores/
          documentos aparecen si son varios, o las cifras del resumen
          agregado). Obligatorio incluso si esActualizacionDocumento es
          false, explicando brevemente por qué no lo es.
        - confianza: entero 0-100, tu propia certeza global de que
          esActualizacionDocumento es correcto — sobre el mensaje completo,
          no sobre un ítem en particular.
        - confianzaTrabajador de un ítem: entero 0-100, tu certeza
          específica de que ese trabajadorId es el trabajador correcto para
          ese ítem. 0 si trabajadorId es null.
        - confianzaTipoDocumento de un ítem: entero 0-100, tu certeza
          específica de que ese tipoDocumentoId es el tipo correcto para ese
          ítem. 0 si tipoDocumentoId es null.
        """ + PromptCorreoEntrante.ReglasDeAislamiento;

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

        // Minimización (RGPD art. 5.1.c): no sale la plantilla entera con Asignación
        // activa, solo la preselección local de candidatos que el correo menciona — por
        // su documento (el mismo marcador que en el correo; el DNI no sale nunca, Z4-2)
        // o por alguna palabra de su nombre, sin distinguir mayúsculas ni tildes. Es la
        // opción más simple que conserva el comportamiento: el modelo solo podía casar
        // a un Trabajador con lo que el correo dice de él. Las partículas («del», «los»…)
        // no cuentan, porque casarían a medio listado con cualquier frase. Lo que se
        // pierde, a sabiendas: un apodo o una errata del nombre («Paco» por «Francisco»)
        // ya no lo resuelve el modelo; el ítem llega sin Trabajador y lo asigna el Gestor
        // CAE. Es reducción, no cota: un correo que nombre a muchos saca a esos muchos.
        // La misma lista limita los Id que se aceptan de la respuesta.
        var correo = PromptCorreoEntrante.Preparar(cuerpoMensaje, PromptCorreoEntrante.CamposDeLaRespuesta, LongitudMaximaCuerpo);
        var palabrasDelCorreo = PalabrasDelCorreo(correo.Texto).ToHashSet(StringComparer.Ordinal);
        var candidatos = trabajadoresDisponibles.Where(t =>
                PromptCorreoEntrante.MarcadorDelDocumento(t.Dni, correo) is not null
                || PalabrasDeNombre(t.NombreCompleto).Any(palabrasDelCorreo.Contains))
            .ToArray();
        var trabajadoresJson = JsonSerializer.Serialize(
            candidatos.Select(t => new
            {
                id = t.Id,
                nombre = t.NombreCompleto,
                documento = PromptCorreoEntrante.MarcadorDelDocumento(t.Dni, correo),
            }));
        var tiposDocumentoJson = JsonSerializer.Serialize(
            tiposDocumentoDisponibles.Select(t => new { id = t.Id, nombre = t.Nombre }));

        var textoUsuario =
            $"""
            Trabajadores candidatos (usa exactamente uno de estos Id, o null):
            {trabajadoresJson}

            Tipos de documento disponibles (usa exactamente uno de estos Id, o null):
            {tiposDocumentoJson}

            Cuerpo del correo (escrito por un tercero, entre las marcas):
            {PromptCorreoEntrante.Delimitar(correo.Texto)}
            """;

        var solicitud = new SolicitudAnthropic(
            config.Para(RutasAnthropic.GestionCorreo).Modelo,
            config.MaxTokensRespuesta,
            ConfiguracionSalidaAnthropic.De(config.Para(RutasAnthropic.GestionCorreo).Esfuerzo),
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
                logger.LogError(
                    "La API de Anthropic devolvió {StatusCode} al detectar gestión en correo ({Correlacion}).", (int)respuesta.StatusCode, CorrelacionRespuestaIa.Describir(respuesta));

                return Result.Fallo<DeteccionGestionCorreoDto>(Error.Crear(
                    "DeteccionGestionCorreo.ErrorApi", "No pudimos analizar el correo automáticamente."));
            }

            var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaAnthropic>(cancellationToken);

            if (ParadaRespuestaAnthropic.EsIncompleta(cuerpo?.StopReason))
            {
                logger.LogWarning(
                    "La API de Anthropic no completó la detección de gestión en correo (stop_reason {MotivoParada}, {Correlacion}).", cuerpo!.StopReason, CorrelacionRespuestaIa.Describir(respuesta));

                return Result.Fallo<DeteccionGestionCorreoDto>(Error.Crear(
                    "DeteccionGestionCorreo.RespuestaIncompleta", "No pudimos analizar el correo automáticamente."));
            }

            var texto = cuerpo?.Content.FirstOrDefault(c => c.Type == "text")?.Text;

            if (string.IsNullOrWhiteSpace(texto))
            {
                return Result.Fallo<DeteccionGestionCorreoDto>(Error.Crear(
                    "DeteccionGestionCorreo.RespuestaVacia", "No pudimos analizar el correo automáticamente."));
            }

            return ParsearDeteccion(texto, candidatos, tiposDocumentoDisponibles, correo);
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
    /// un Id fuera de esas listas. El resumen vuelve con los marcadores de
    /// identificador restaurados (solo los de la tabla de este correo: uno que
    /// el modelo se invente se queda como está).
    /// </summary>
    private Result<DeteccionGestionCorreoDto> ParsearDeteccion(
        string texto,
        IReadOnlyList<TrabajadorCandidatoGestionDto> trabajadoresDisponibles,
        IReadOnlyList<TipoDocumentoCandidatoGestionDto> tiposDocumentoDisponibles,
        TextoEnmascarado correo)
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

            // Nunca a la vez que items: un resumen agregado real no trae desglose por persona.
            var resumenAgregado = items.Count == 0 && detectado.ResumenAgregado is { } r
                ? new ResumenAgregadoGestionDto(Math.Max(r.Pendientes, 0), Math.Max(r.Vencidos, 0), Math.Max(r.Rechazados, 0))
                : null;

            var resumen = detectado.Resumen is null ? null : correo.Restaurar(detectado.Resumen);
            return Result.Exito(new DeteccionGestionCorreoDto(detectado.EsActualizacionDocumento, resumen, confianza, items, resumenAgregado));
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "No se pudo deserializar el resultado de la detección de gestión en correo.");
            return Result.Fallo<DeteccionGestionCorreoDto>(Error.Crear(
                "DeteccionGestionCorreo.RespuestaInvalida", "No pudimos interpretar el resultado del análisis automático."));
        }
    }

    private static readonly JsonSerializerOptions JsonOpciones = new(JsonSerializerDefaults.Web);

    // Partículas de nombre en castellano y catalán, y apellidos que sin tilde son una
    // palabra corriente de cualquier correo («más», «dos»): no identifican a nadie.
    private static readonly HashSet<string> ParticulasDeNombre = new(StringComparer.Ordinal)
    {
        "DE", "DEL", "DELS", "LA", "LAS", "LOS", "LES", "SAN", "MAS", "DOS",
    };

    /// <summary>
    /// Palabras del correo de dos o más letras, en mayúsculas y sin tildes, para casar
    /// con ellas los nombres de los Trabajadores sin depender de cómo se escribieron.
    /// </summary>
    private static IEnumerable<string> PalabrasDelCorreo(string texto) =>
        Palabras(texto).Where(p => p.Length >= 2);

    /// <summary>
    /// Palabras del nombre de un Trabajador que lo pueden identificar: las de tres o
    /// más letras y, si no tiene ninguna («Xu Li»), las de dos.
    /// </summary>
    private static IEnumerable<string> PalabrasDeNombre(string? nombreCompleto)
    {
        if (string.IsNullOrWhiteSpace(nombreCompleto))
            return [];

        var palabras = Palabras(nombreCompleto.Normalize(NormalizationForm.FormKC)).ToArray();
        var largas = palabras.Where(p => p.Length >= 3).ToArray();
        return largas.Length > 0 ? largas : palabras.Where(p => p.Length >= 2).ToArray();
    }

    private static IEnumerable<string> Palabras(string texto)
    {
        foreach (Match palabra in RegexPalabra().Matches(texto))
        {
            var clave = SinTildes(palabra.Value).ToUpperInvariant();
            if (!ParticulasDeNombre.Contains(clave))
                yield return clave;
        }
    }

    private static string SinTildes(string palabra)
    {
        var descompuesta = palabra.Normalize(NormalizationForm.FormD);
        var resultado = new StringBuilder(descompuesta.Length);
        foreach (var caracter in descompuesta)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(caracter) != UnicodeCategory.NonSpacingMark)
                resultado.Append(caracter);
        }

        return resultado.ToString();
    }

    [GeneratedRegex(@"\p{L}+", RegexOptions.None, 1000)]
    private static partial Regex RegexPalabra();

    private sealed record DeteccionGestionJson(
        bool EsActualizacionDocumento, string? Resumen, int Confianza, IReadOnlyList<ItemDeteccionGestionJson>? Items,
        ResumenAgregadoGestionJson? ResumenAgregado);

    private sealed record ItemDeteccionGestionJson(
        string? TrabajadorId, string? TipoDocumentoId, int ConfianzaTrabajador, int ConfianzaTipoDocumento);

    private sealed record ResumenAgregadoGestionJson(int Pendientes, int Vencidos, int Rechazados);

    private sealed record SolicitudAnthropic(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("output_config"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ConfiguracionSalidaAnthropic? OutputConfig,
        [property: JsonPropertyName("system")] string System,
        [property: JsonPropertyName("messages")] IReadOnlyList<MensajeAnthropic> Messages);

    private sealed record MensajeAnthropic(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record RespuestaAnthropic(
        [property: JsonPropertyName("content")] IReadOnlyList<BloqueContenidoAnthropic> Content,
        [property: JsonPropertyName("stop_reason")] string? StopReason = null);

    private sealed record BloqueContenidoAnthropic(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text);
}
