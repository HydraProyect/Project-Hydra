namespace CaeManager.Domain.DocumentosIa;

/// <summary>
/// Qué pasó después de que la IA propusiera una extracción — el cierre del
/// ciclo "la IA propone, el humano dispone" sobre <see cref="AuditoriaExtraccionIa"/>
/// (MACRO_PLAN § 6.6, "registro por operación"). Solo se registra cuando la
/// extracción quedó ligada a un Documento concreto (ver
/// VerificacionIaDocumentoService) — las lecturas de mero triage, antes de
/// que exista Documento, no tienen decisión humana que enlazar todavía.
/// </summary>
public enum DecisionHumanaIa
{
    /// <summary>Confianza suficiente y sin discrepancias: se dio por buena sin que ningún humano la revisara (ver AprobacionDocumento.CrearAutomatica).</summary>
    AutomaticaSinRevision,

    /// <summary>Un Gestor CAE revisó la propuesta de la IA y la aplicó al Documento tal cual (ver AplicarDeteccionIaDocumentoCommand).</summary>
    ConfirmadaManual,

    /// <summary>Un Gestor CAE revisó la propuesta de la IA y decidió no aplicarla — el Documento conserva el dato que ya tenía (ver ResolverRevisionIaDocumentoCommand).</summary>
    DescartadaManual
}
