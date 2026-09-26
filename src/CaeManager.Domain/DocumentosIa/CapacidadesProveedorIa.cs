namespace CaeManager.Domain.DocumentosIa;

/// <summary>
/// Qué sabe hacer un proveedor de IA documental — el enrutador (fases
/// siguientes de Project-Hydra-Negocio/tecnico/docs/ARQUITECTURA-IA-DOCUMENTAL.md) decide contra estas
/// capacidades, nunca contra el nombre de un proveedor concreto. Mismo
/// principio que <c>CapacidadesIntegracion</c> (Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md
/// § 3.1) aplicado a IA en vez de a conectores CAE.
/// </summary>
[Flags]
public enum CapacidadesProveedorIa
{
    Ninguna = 0,

    /// <summary>OCR: convierte una página escaneada/imagen en texto plano (p. ej. Mistral OCR).</summary>
    OcrImagenAEscaneado = 1 << 0,

    /// <summary>Extracción estructurada con confidence score a partir de texto ya disponible (p. ej. Gemini 2.5 Flash).</summary>
    ExtraccionEstructurada = 1 << 1,

    /// <summary>Clasificación barata de tipo de documento, sin extracción completa — sin construir todavía.</summary>
    ClasificacionBarata = 1 << 2,

    /// <summary>Comparación entre dos versiones de un mismo documento (Issue #19) — sin construir todavía.</summary>
    ComparacionDocumentos = 1 << 3,
}
