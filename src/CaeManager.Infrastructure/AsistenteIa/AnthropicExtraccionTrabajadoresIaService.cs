using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.AsistenteIa;

/// <summary>
/// Lectura por IA de documentos PDF de Empresa (ITA, RNT/TC2, y cualquier
/// otro tipo de documento con lectura IA activa) para extraer el listado de
/// trabajadores dados de alta — ver DeteccionTrabajadoresService, que la
/// usa para detectar altas/bajas de personal (Fase 36). Reutiliza
/// <see cref="AnthropicOptions"/> (misma ApiKey/modelo que el asistente de
/// chat): sin ApiKey configurada, queda inerte igual que
/// AnthropicAsistenteIaService — la subida del documento sigue funcionando,
/// solo sin detección automática. El PDF se envía como bloque de contenido
/// "document" (soportado nativamente por los modelos Claude actuales, sin
/// necesidad de convertirlo a imágenes por página).
/// </summary>
public class AnthropicExtraccionTrabajadoresIaService(
    HttpClient httpClient,
    IOptions<AnthropicOptions> opciones,
    ILogger<AnthropicExtraccionTrabajadoresIaService> logger) : IExtraccionTrabajadoresIaService
{
    private const string SystemPrompt =
        """
        Eres un sistema de extracción de datos, no un asistente conversacional.

        Se te proporciona un documento oficial español de Seguridad Social
        (por ejemplo un ITA — Informe de Trabajadores en Alta, o un RNT/TC2 —
        Relación Nominal de Trabajadores) que contiene un listado de
        trabajadores dados de alta en una empresa.

        Tu única tarea es extraer ese listado y devolverlo en formato JSON
        estricto, sin ningún texto adicional, sin explicaciones, sin bloques
        de código markdown — solo el array JSON.

        Formato exacto de salida (un array JSON, nada más):
        [{"nombre": "...", "apellidos": "...", "dni": "..."}]

        Reglas:
        - nombre y apellidos: tal como aparecen en el documento.
        - dni: el número de DNI/NIE tal como aparece, sin espacios, en mayúsculas.
        - Si el documento no contiene ningún trabajador identificable, devuelve un array vacío: []
        - No inventes datos que no estén en el documento.
        - No incluyas ningún trabajador cuyo DNI no puedas leer con certeza razonable.
        """;

    public async Task<Result<IReadOnlyList<TrabajadorExtraidoDto>>> ExtraerAsync(byte[] contenidoPdf, CancellationToken cancellationToken = default)
    {
        var config = opciones.Value;

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return Result.Fallo<IReadOnlyList<TrabajadorExtraidoDto>>(Error.Crear(
                "ExtraccionTrabajadores.NoConfigurado", "La lectura automática por IA no está disponible ahora mismo."));
        }

        var solicitud = new SolicitudAnthropic(
            config.Modelo,
            config.MaxTokensRespuesta,
            SystemPrompt,
            [
                new MensajeAnthropic("user",
                [
                    new BloqueContenido("document", new FuenteDocumento("base64", "application/pdf", Convert.ToBase64String(contenidoPdf)), null),
                    new BloqueContenido("text", null, "Extrae el listado de trabajadores de este documento, siguiendo exactamente el formato indicado.")
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
                    "La API de Anthropic devolvió {StatusCode} al extraer trabajadores: {Cuerpo}", (int)respuesta.StatusCode, cuerpoError);

                return Result.Fallo<IReadOnlyList<TrabajadorExtraidoDto>>(Error.Crear(
                    "ExtraccionTrabajadores.ErrorApi", "No pudimos leer el documento automáticamente."));
            }

            var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaAnthropic>(cancellationToken);
            var texto = cuerpo?.Content.FirstOrDefault(c => c.Type == "text")?.Text;

            if (string.IsNullOrWhiteSpace(texto))
            {
                return Result.Fallo<IReadOnlyList<TrabajadorExtraidoDto>>(Error.Crear(
                    "ExtraccionTrabajadores.RespuestaVacia", "No pudimos leer el documento automáticamente."));
            }

            return ParsearListado(texto);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al contactar la API de Anthropic para extracción de trabajadores.");
            return Result.Fallo<IReadOnlyList<TrabajadorExtraidoDto>>(Error.Crear(
                "ExtraccionTrabajadores.ErrorRed", "No pudimos leer el documento automáticamente."));
        }
    }

    /// <summary>
    /// El modelo, pese a la instrucción, a veces envuelve el JSON en un
    /// bloque de código markdown — se busca el primer '[' y el último ']'
    /// como red de seguridad antes de intentar deserializar.
    /// </summary>
    private Result<IReadOnlyList<TrabajadorExtraidoDto>> ParsearListado(string texto)
    {
        var inicio = texto.IndexOf('[');
        var fin = texto.LastIndexOf(']');

        if (inicio < 0 || fin < inicio)
        {
            logger.LogWarning("La respuesta de extracción de trabajadores no contenía un array JSON reconocible.");
            return Result.Fallo<IReadOnlyList<TrabajadorExtraidoDto>>(Error.Crear(
                "ExtraccionTrabajadores.RespuestaInvalida", "No pudimos interpretar el resultado de la lectura automática."));
        }

        var json = texto[inicio..(fin + 1)];

        try
        {
            var extraidos = JsonSerializer.Deserialize<List<TrabajadorExtraidoJson>>(json, JsonOpciones) ?? [];

            IReadOnlyList<TrabajadorExtraidoDto> resultado = extraidos
                .Where(e => !string.IsNullOrWhiteSpace(e.Dni))
                .Select(e => new TrabajadorExtraidoDto(e.Nombre ?? string.Empty, e.Apellidos ?? string.Empty, e.Dni!.Trim()))
                .ToList();

            return Result.Exito(resultado);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "No se pudo deserializar el resultado de la extracción de trabajadores.");
            return Result.Fallo<IReadOnlyList<TrabajadorExtraidoDto>>(Error.Crear(
                "ExtraccionTrabajadores.RespuestaInvalida", "No pudimos interpretar el resultado de la lectura automática."));
        }
    }

    private static readonly JsonSerializerOptions JsonOpciones = new(JsonSerializerDefaults.Web);

    private sealed record TrabajadorExtraidoJson(string? Nombre, string? Apellidos, string? Dni);

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
