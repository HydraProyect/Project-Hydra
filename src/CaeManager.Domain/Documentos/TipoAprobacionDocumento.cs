namespace CaeManager.Domain.Documentos;

/// <summary>Quién dio por buena la verificación IA de un Documento — ver AprobacionDocumento.</summary>
public enum TipoAprobacionDocumento
{
    /// <summary>Confianza de IA suficiente y sin discrepancias — aceptado sin que ningún humano lo revisara.</summary>
    Automatica,

    /// <summary>Confianza baja o discrepancia — un Gestor CAE revisó y resolvió la RevisionIaDocumento pendiente.</summary>
    Manual
}
