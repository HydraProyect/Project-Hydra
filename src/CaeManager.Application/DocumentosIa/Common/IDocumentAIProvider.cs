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
    /// Si este proveedor puede atender una llamada ahora mismo — en la
    /// práctica, si tiene credencial configurada. Deliberadamente separado de
    /// <see cref="Capacidades"/>: un proveedor sin API key sigue siendo capaz
    /// de OCR "en abstracto", pero incluirlo en la lista de candidatos hace
    /// que el router lo elija y falle sin haber intentado ninguno de los que
    /// sí podían responder.
    ///
    /// Sin propiedad, ni <see cref="IDocumentAIProviderFactory"/> ni el router
    /// tenían forma de distinguir "no configurado" de "configurado y caído":
    /// los tres proveedores reales declaraban sus capacidades siempre, y el
    /// primero de la lista se llevaba el trabajo aunque su clave estuviera
    /// vacía. No tiene implementación por defecto a propósito — un proveedor
    /// nuevo tiene que pronunciarse sobre cuándo está disponible, no
    /// heredarlo por descuido.
    /// </summary>
    bool EstaDisponible { get; }

    /// <summary>
    /// OCR/lectura nativa: extrae el texto plano de un archivo completo
    /// (PDF escaneado o imagen suelta — <paramref name="nombreArchivo"/> es
    /// lo que decide cómo se envía al proveedor, p. ej. como bloque
    /// "document" o "image" en Anthropic). Requiere
    /// <see cref="CapacidadesProveedorIa.OcrImagenAEscaneado"/>. El coste
    /// estimado en <see cref="TextoExtraccionDto"/> es null si el proveedor
    /// no lo puede calcular con los datos de la respuesta.
    /// </summary>
    Task<Result<TextoExtraccionDto>> ExtraerTextoAsync(byte[] contenidoArchivo, string nombreArchivo, CancellationToken cancellationToken = default);

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
/// <summary>
/// <paramref name="CosteEstimado"/> (en USD, null si el proveedor no lo
/// calcula) es solo un dato de auditoría — nunca un criterio de enrutado
/// (ver docs/ARQUITECTURA-IA-DOCUMENTAL.md § 4.2). Cada proveedor lo
/// calcula con su propia unidad de precio.
/// </summary>
public record ExtraccionEstructuradaDto(
    string? TipoDetectado, IReadOnlyDictionary<string, string?> Campos, int ConfianzaGeneral, string? NotasValidacion,
    decimal? CosteEstimado = null);

/// <summary>
/// Resultado del paso de OCR/lectura nativa de <see cref="IDocumentAIProvider.ExtraerTextoAsync"/>:
/// el texto plano extraído y el coste estimado del paso (null si el proveedor
/// no lo calcula). Coste solo para auditoría — ver docs/ARQUITECTURA-IA-DOCUMENTAL.md § 4.2.
/// </summary>
public record TextoExtraccionDto(string Texto, decimal? CosteEstimado = null);
