using CaeManager.Domain.Common;

namespace CaeManager.Application.Common;

/// <summary>
/// Extrae el texto ya embebido de un PDF digital — sin IA, sin OCR (ver
/// docs/ARQUITECTURA-IA-DOCUMENTAL.md, Caso 1: "PDF con texto digital →
/// Gemini directo, no ejecutar OCR"). Solo tiene sentido llamarlo sobre
/// páginas que <see cref="IClasificadorDocumentoService"/> ya determinó
/// como <see cref="TipoContenidoDocumento.Digital"/> — sobre una página
/// escaneada simplemente no habrá texto que extraer.
/// </summary>
public interface IExtractorTextoDigitalService
{
    Result<string> ExtraerTexto(byte[] contenidoPdf);
}
