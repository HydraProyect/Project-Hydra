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
/// docs/ARQUITECTURA-IA-DOCUMENTAL.md § 4.1) — declara solo
/// <see cref="CapacidadesProveedorIa.OcrImagenAEscaneado"/>, siguiendo el
/// reparto de capacidades documentado (Mistral = OCR, Gemini =
/// estructuración) hasta que exista un benchmark real que diga lo
/// contrario. Mismo patrón "inerte por defecto" que
/// <see cref="AnthropicDocumentAIProvider"/>/<see cref="GeminiDocumentAIProvider"/>:
/// sin <see cref="MistralOcrOptions.ApiKey"/>, cada método falla con un
/// error controlado, nunca lanza.
///
/// <c>ExtraerEstructuradoAsync</c> NO implementa la función "Document AI"
/// de Mistral (extracción estructurada por esquema JSON, ver la nota de
/// Fase 46 en ROADMAP.md) — varias páginas oficiales de Mistral
/// bloquearon la verificación directa del formato exacto de esa petición
/// (403) durante la implementación, y no se quiso adivinar un contrato
/// HTTP no confirmado. Devuelve un fallo controlado explícito; se
/// completa cuando se pueda verificar el formato real (documentación
/// accesible o prueba con la clave real).
/// </summary>
public class MistralOcrDocumentAIProvider(
    HttpClient httpClient,
    IOptions<MistralOcrOptions> opciones,
    ILogger<MistralOcrDocumentAIProvider> logger) : IDocumentAIProvider
{
    public string Codigo => "mistral-ocr";

    public CapacidadesProveedorIa Capacidades => CapacidadesProveedorIa.OcrImagenAEscaneado;

    public async Task<Result<string>> ExtraerTextoAsync(byte[] contenidoArchivo, string nombreArchivo, CancellationToken cancellationToken = default)
    {
        var config = opciones.Value;

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return Result.Fallo<string>(Error.Crear(
                "DocumentAIProvider.NoConfigurado", "La lectura automática por IA no está disponible ahora mismo."));
        }

        var esPdf = nombreArchivo.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        var mimeType = esPdf ? "application/pdf" : DetectarTipoImagen(contenidoArchivo);
        var dataUrl = $"data:{mimeType};base64,{Convert.ToBase64String(contenidoArchivo)}";
        var documento = esPdf
            ? new DocumentoMistral("document_url", dataUrl, null)
            : new DocumentoMistral("image_url", null, dataUrl);

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
                var cuerpoError = await respuesta.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("La API de Mistral OCR devolvió {StatusCode}: {Cuerpo}", (int)respuesta.StatusCode, cuerpoError);
                return Result.Fallo<string>(Error.Crear("DocumentAIProvider.ErrorApi", "No pudimos procesar el documento automáticamente."));
            }

            var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaOcrMistral>(cancellationToken);
            var paginas = cuerpo?.Pages;

            if (paginas is null || paginas.Count == 0)
            {
                return Result.Fallo<string>(Error.Crear("DocumentAIProvider.RespuestaVacia", "No pudimos procesar el documento automáticamente."));
            }

            return Result.Exito(string.Join("\n\n", paginas.Select(p => p.Markdown ?? string.Empty)).Trim());
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al contactar la API de Mistral OCR.");
            return Result.Fallo<string>(Error.Crear("DocumentAIProvider.ErrorRed", "No pudimos procesar el documento automáticamente."));
        }
    }

    /// <summary>Ver el comentario de clase: la extracción estructurada de Mistral ("Document AI") no se implementó por no poder verificar su contrato HTTP exacto — no se activa (§ Capacidades) y aquí devuelve un fallo explícito si algo llegara a invocarla igualmente.</summary>
    public Task<Result<ExtraccionEstructuradaDto>> ExtraerEstructuradoAsync(
        string texto, string tipoEsperado, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear(
            "DocumentAIProvider.NoSoportado", "Este proveedor no realiza extracción estructurada todavía.")));

    private static string DetectarTipoImagen(byte[] contenido)
    {
        if (contenido.Length >= 8 && contenido[0] == 0x89 && contenido[1] == 0x50 && contenido[2] == 0x4E && contenido[3] == 0x47)
            return "image/png";
        if (contenido.Length >= 3 && contenido[0] == 0xFF && contenido[1] == 0xD8)
            return "image/jpeg";
        return "image/jpeg";
    }

    private sealed record SolicitudOcrMistral(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("document")] DocumentoMistral Document);

    private sealed record DocumentoMistral(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("document_url")] string? DocumentUrl,
        [property: JsonPropertyName("image_url")] string? ImageUrl);

    private sealed record RespuestaOcrMistral([property: JsonPropertyName("pages")] IReadOnlyList<PaginaMistral>? Pages);

    private sealed record PaginaMistral([property: JsonPropertyName("markdown")] string? Markdown);
}
