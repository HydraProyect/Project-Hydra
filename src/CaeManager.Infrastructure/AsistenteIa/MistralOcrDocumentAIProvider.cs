using System.Net.Http.Headers;
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
/// Tercer <see cref="IDocumentAIProvider"/> real (ver
/// docs/ARQUITECTURA-IA-DOCUMENTAL.md § 4.1) — declara
/// <see cref="CapacidadesProveedorIa.OcrImagenAEscaneado"/> y
/// <see cref="CapacidadesProveedorIa.ExtraccionEstructurada"/>. OCR vía
/// la API <c>/v1/ocr</c> de Mistral; estructuración vía
/// <c>/v1/chat/completions</c> con el modelo configurado en
/// <see cref="MistralOcrOptions.ModeloChat"/> (por defecto
/// <c>mistral-small-latest</c>) — mismo enfoque texto→JSON que Anthropic
/// y Gemini, sin necesidad de enviar el documento en bruto. Mismo patrón
/// "inerte por defecto": sin <see cref="MistralOcrOptions.ApiKey"/>,
/// cada método falla con un error controlado, nunca lanza.
/// </summary>
public class MistralOcrDocumentAIProvider(
    HttpClient httpClient,
    IOptions<MistralOcrOptions> opciones,
    ILogger<MistralOcrDocumentAIProvider> logger) : IDocumentAIProvider
{
    public string Codigo => "mistral-ocr";

    public CapacidadesProveedorIa Capacidades =>
        CapacidadesProveedorIa.OcrImagenAEscaneado | CapacidadesProveedorIa.ExtraccionEstructurada;

    /// <summary>Inerte sin credencial: sin ApiKey configurada cada método ya devolvía un Result fallido, pero el router no lo sabía hasta después de haberlo elegido. Ahora no se elige.</summary>
    public bool EstaDisponible => !string.IsNullOrWhiteSpace(opciones.Value.ApiKey);

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

        {"tipoDetectado": "...", "campos": {"fechaEmision": "YYYY-MM-DD" o ausente, "fechaVencimiento": "YYYY-MM-DD" o ausente, "tieneFirma": "true"/"false" o ausente, "nombreDocumento": "..." o ausente, "nombreEmpresa": "..." o ausente, "cifEmpresa": "..." o ausente, "rolEmpresa": "principal"/"subcontratada" o ausente, "nombreTrabajador": "..." o ausente, "documentoIdentidadTrabajador": "..." o ausente, "tipoCertificacion": "..." o ausente, "articuloLey": "..." o ausente, "especificoPuestoTrabajo": "true"/"false" o ausente, "nombreDeCampo": "valor", ...}, "confianzaGeneral": 0-100, "notasValidacion": "..." o null}

        Reglas:
        - "fechaEmision"/"fechaVencimiento" (si aparecen explícitas, formato
          ISO YYYY-MM-DD) y "tieneFirma" ("true" si detectas una firma
          física/digital/electrónica, "false" si claramente no hay ninguna)
          son campos comunes a casi cualquier documento CAE — inclúyelos
          cuando puedas determinarlos.
        - Si el documento no indica una fecha de vencimiento explícita pero
          sí establece un periodo de vigencia contado desde la fecha de
          emisión (p. ej. "válido por 2 años"), calcula "fechaVencimiento"
          sumando ese periodo a "fechaEmision" — nunca inventes un periodo
          que no esté indicado en el texto.
        - "nombreDocumento": el título o denominación del documento tal
          como aparece en él (p. ej. "Certificado de formación en trabajos
          en altura"), no el nombre de archivo.
        - "nombreEmpresa"/"cifEmpresa": nombre y CIF de la empresa a la que
          pertenece o que emite el documento, si aparece. "rolEmpresa":
          "principal" si el documento identifica a esa empresa como el
          cliente/contratista principal que encarga el trabajo,
          "subcontratada" si la identifica como subcontrata que presta el
          servicio — solo si el documento distingue explícitamente ese rol.
        - "nombreTrabajador"/"documentoIdentidadTrabajador": nombre
          completo y número de documento de identidad (DNI, NIE o
          pasaporte, tal cual aparece, sin normalizar el formato) del
          trabajador al que se refiere el documento, si aplica.
        - "tipoCertificacion": solo si el documento es una certificación o
          acreditación de formación (p. ej. "trabajo en altura", "espacios
          confinados", "plataformas elevadoras"), indica de qué tipo.
        - "articuloLey": artículo o normativa concreta (ley, real decreto,
          convenio) que el documento cubre o justifica, si se menciona
          explícitamente.
        - "especificoPuestoTrabajo": "true" si el documento de formación en
          riesgos laborales está redactado específicamente para el puesto
          de trabajo del técnico al que se refiere (no una formación
          genérica), "false" si es genérica o no se puede determinar el
          puesto concreto.
        - Además de los anteriores, "campos" contiene todos los demás
          datos relevantes que puedas extraer con certeza razonable
          (importes, números de referencia, coberturas, etc.), como pares
          clave/valor de texto — usa nombres de campo descriptivos en
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

        var esPdf = nombreArchivo.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        object documento;
        if (esPdf)
        {
            var dataUrl = $"data:application/pdf;base64,{Convert.ToBase64String(contenidoArchivo)}";
            documento = new DocumentoPdfMistral("document_url", dataUrl);
        }
        else
        {
            var mimeType = DetectarTipoImagen(contenidoArchivo);
            var dataUrl = $"data:{mimeType};base64,{Convert.ToBase64String(contenidoArchivo)}";
            documento = new DocumentoImagenMistral("image_url", new ImagenUrlDetalleMistral("image_url", dataUrl));
        }

        using var peticion = new HttpRequestMessage(HttpMethod.Post, "https://api.mistral.ai/v1/ocr")
        {
            Content = JsonContent.Create(new SolicitudOcrMistral(config.Modelo, documento))
        };
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);

            if (!respuesta.IsSuccessStatusCode)
            {
                logger.LogError(
                    "La API de Mistral OCR devolvió {StatusCode} ({Correlacion}).", (int)respuesta.StatusCode, CorrelacionRespuestaIa.Describir(respuesta));
                return Result.Fallo<TextoExtraccionDto>(Error.Crear("DocumentAIProvider.ErrorApi", "No pudimos procesar el documento automáticamente."));
            }

            var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaOcrMistral>(cancellationToken);
            var paginas = cuerpo?.Pages;

            if (paginas is null || paginas.Count == 0)
            {
                return Result.Fallo<TextoExtraccionDto>(Error.Crear("DocumentAIProvider.RespuestaVacia", "No pudimos procesar el documento automáticamente."));
            }

            var texto = string.Join("\n\n", paginas.Select(p => p.Markdown ?? string.Empty)).Trim();
            var paginasProcesadas = cuerpo?.UsageInfo?.PaginasProcesadas ?? paginas.Count;
            var coste = paginasProcesadas / 1000m * config.CostoPorMilPaginasOcr;
            return Result.Exito(new TextoExtraccionDto(texto, coste));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al contactar la API de Mistral OCR.");
            return Result.Fallo<TextoExtraccionDto>(Error.Crear("DocumentAIProvider.ErrorRed", "No pudimos procesar el documento automáticamente."));
        }
    }

    public async Task<Result<ExtraccionEstructuradaDto>> ExtraerEstructuradoAsync(
        string texto, string tipoEsperado, CancellationToken cancellationToken = default)
    {
        var config = opciones.Value;

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear(
                "DocumentAIProvider.NoConfigurado", "La extracción automática por IA no está disponible ahora mismo."));
        }

        var solicitud = new SolicitudChatMistral(
            config.ModeloChat,
            [
                new MensajeMistral("system", SystemPromptEstructurado + PromptDocumental.ReglasDeAislamiento),
                new MensajeMistral("user", PromptDocumental.ConstruirMensajeUsuario(tipoEsperado, texto))
            ],
            config.MaxTokensRespuestaChat);

        using var peticion = new HttpRequestMessage(HttpMethod.Post, "https://api.mistral.ai/v1/chat/completions")
        {
            Content = JsonContent.Create(solicitud)
        };
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);

            if (!respuesta.IsSuccessStatusCode)
            {
                logger.LogError(
                    "La API de Mistral chat devolvió {StatusCode} ({Correlacion}).", (int)respuesta.StatusCode, CorrelacionRespuestaIa.Describir(respuesta));
                return Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear("DocumentAIProvider.ErrorApi", "No pudimos procesar el documento automáticamente."));
            }

            var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaChatMistral>(cancellationToken);
            var textoRespuesta = cuerpo?.Choices?.FirstOrDefault()?.Message?.Content;

            if (string.IsNullOrWhiteSpace(textoRespuesta))
            {
                return Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear("DocumentAIProvider.RespuestaVacia", "No pudimos procesar el documento automáticamente."));
            }

            var coste = (cuerpo?.Usage?.TokensEntrada ?? 0) / 1_000_000m * config.CostoPorMillonTokensEntradaChat
                      + (cuerpo?.Usage?.TokensSalida ?? 0) / 1_000_000m * config.CostoPorMillonTokensSalidaChat;

            return ParsearEstructurado(textoRespuesta, coste);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al contactar la API de Mistral chat.");
            return Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear("DocumentAIProvider.ErrorRed", "No pudimos procesar el documento automáticamente."));
        }
    }

    private Result<ExtraccionEstructuradaDto> ParsearEstructurado(string texto, decimal costeEstimado)
    {
        var inicio = texto.IndexOf('{');
        var fin = texto.LastIndexOf('}');

        if (inicio < 0 || fin < inicio)
        {
            logger.LogWarning("La respuesta de extracción estructurada de Mistral no contenía un objeto JSON reconocible.");
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
            logger.LogWarning(ex, "No se pudo deserializar el resultado de la extracción estructurada de Mistral.");
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

    private sealed record SolicitudChatMistral(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<MensajeMistral> Messages,
        [property: JsonPropertyName("max_tokens")] int MaxTokens);

    private sealed record MensajeMistral(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record RespuestaChatMistral(
        [property: JsonPropertyName("choices")] IReadOnlyList<OpcionChatMistral>? Choices,
        [property: JsonPropertyName("usage")] UsoChatMistral? Usage);

    private sealed record OpcionChatMistral(
        [property: JsonPropertyName("message")] MensajeMistral? Message);

    private sealed record UsoChatMistral(
        [property: JsonPropertyName("prompt_tokens")] int TokensEntrada,
        [property: JsonPropertyName("completion_tokens")] int TokensSalida);

    private sealed record SolicitudOcrMistral(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("document")] object Document);

    // DocumentURLChunk: { "type": "document_url", "document_url": "<data-uri>" }
    private sealed record DocumentoPdfMistral(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("document_url")] string DocumentUrl);

    // ImageURLChunk: { "type": "image_url", "image_url": { "type": "image_url", "url": "<data-uri>" } }
    private sealed record DocumentoImagenMistral(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("image_url")] ImagenUrlDetalleMistral ImageUrl);

    private sealed record ImagenUrlDetalleMistral(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("url")] string Url);

    private sealed record RespuestaOcrMistral(
        [property: JsonPropertyName("pages")] IReadOnlyList<PaginaMistral>? Pages,
        [property: JsonPropertyName("usage_info")] UsoMistral? UsageInfo);

    private sealed record PaginaMistral([property: JsonPropertyName("markdown")] string? Markdown);

    private sealed record UsoMistral([property: JsonPropertyName("pages_processed")] int PaginasProcesadas);
}
