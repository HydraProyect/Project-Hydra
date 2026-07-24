using CaeManager.Domain.Common;
using CaeManager.Domain.DocumentosIa;

namespace CaeManager.Application.DocumentosIa.Common;

/// <summary>
/// Un proveedor de IA documental (Anthropic hoy para pruebas puntuales;
/// Gemini/Mistral OCR después, ver docs/ARQUITECTURA-IA-DOCUMENTAL.md § 4.1)
/// — el resto del sistema conoce esta interfaz y las <see cref="Capacidades"/>
/// declaradas, nunca el proveedor concreto. Mismo patrón que
/// <c>IIntegrationProvider</c> (ARQUITECTURA-INTEGRACIONES.md § 4).
/// </summary>
public interface IDocumentAIProvider
{
    /// <summary>Identificador estable del proveedor (p. ej. "anthropic", "gemini-2-5-flash", "mistral-ocr") — nunca el nombre para decidir lógica, solo para registrarlo/resolverlo (ver <see cref="IDocumentAIProviderFactory"/>).</summary>
    string Codigo { get; }

    CapacidadesProveedorIa Capacidades { get; }

    /// <summary>
    /// OCR/lectura nativa: extrae el texto plano de un archivo completo
    /// (PDF escaneado o imagen suelta — <paramref name="nombreArchivo"/> es
    /// lo que decide cómo se envía al proveedor, p. ej. como bloque
    /// "document" o "image" en Anthropic). Requiere
    /// <see cref="CapacidadesProveedorIa.OcrImagenAEscaneado"/>.
    /// </summary>
    Task<Result<string>> ExtraerTextoAsync(byte[] contenidoArchivo, string nombreArchivo, CancellationToken cancellationToken = default);

    /// <summary>Extracción estructurada con confidence score a partir de texto ya disponible (digital o ya pasado por OCR). Requiere <see cref="CapacidadesProveedorIa.ExtraccionEstructurada"/>.</summary>
    Task<Result<ExtraccionEstructuradaDto>> ExtraerEstructuradoAsync(string texto, string tipoEsperado, CancellationToken cancellationToken = default);
}

/// <summary>
/// <paramref name="Campos"/> es deliberadamente un diccionario libre (no un
/// DTO tipado por tipo de documento): a diferencia de
/// <c>MetadatosDocumentoExtraidosDto</c> (Fase 38, específico de Documento
/// de Trabajador), este contrato es genérico para cualquier tipo de
/// documento CAE (póliza, recibo, certificado...) — el motor de reglas de
/// Hydra decide qué campos le importan a cada tipo, no la IA.
/// </summary>
public record ExtraccionEstructuradaDto(
    string? TipoDetectado, IReadOnlyDictionary<string, string?> Campos, int ConfianzaGeneral, string? NotasValidacion);
