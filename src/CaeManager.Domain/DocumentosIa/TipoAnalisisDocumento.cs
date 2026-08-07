namespace CaeManager.Domain.DocumentosIa;

/// <summary>
/// Qué análisis pesado hay que hacer sobre un Documento ya guardado.
/// Valores explícitos: la columna persiste el nombre (HasConversion a
/// string), pero fijarlos blinda el significado ante inserciones futuras
/// en medio del enum.
/// </summary>
public enum TipoAnalisisDocumento
{
    /// <summary>Verificación IA con confidence score (ver Issue #19).</summary>
    VerificacionIa = 0,

    /// <summary>Detección de altas/bajas de personal a partir del documento (Fase 36).</summary>
    DeteccionTrabajadores = 1,

    /// <summary>Validación de documento oficial: verificación criptográfica de firma + extracción determinista + cotejo (PLAN-FIRMA-DIGITAL-PDF.md). Sin IA.</summary>
    VerificacionFirmaDigital = 2
}
