namespace CaeManager.Domain.DocumentosIa;

/// <summary>Qué análisis pesado hay que hacer sobre un Documento ya guardado.</summary>
public enum TipoAnalisisDocumento
{
    /// <summary>Verificación IA con confidence score (ver Issue #19).</summary>
    VerificacionIa,

    /// <summary>Detección de altas/bajas de personal a partir del documento (Fase 36).</summary>
    DeteccionTrabajadores
}
