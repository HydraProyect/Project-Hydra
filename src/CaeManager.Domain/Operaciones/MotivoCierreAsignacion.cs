namespace CaeManager.Domain.Operaciones;

/// <summary>
/// Por qué terminó una asignación. Obligatorio al cerrar: es la mitad de la
/// respuesta a "¿por qué dejó ArcosSPA de operar esto?", y sin él el histórico
/// append-only registra el cuándo pero no el porqué.
/// </summary>
public enum MotivoCierreAsignacion
{
    /// <summary>Decisión explícita del propietario de retirar la autorización.</summary>
    Revocada = 0,

    /// <summary>Llegó su <c>VigenciaHasta</c>. Lo aplica el job de expiración.</summary>
    Expirada = 1,

    /// <summary>Traspaso a otro operador sobre el mismo ámbito (ADR-011 § 4.5).</summary>
    Transferida = 2,

    /// <summary>Cambio de reparto interno: el ámbito se partió o se reagrupó.</summary>
    Reorganizada = 3,

    /// <summary>
    /// El objeto al que apuntaba su ámbito se eliminó (soft delete). Sin este
    /// cierre, la asignación seguiría vigente sobre algo invisible, ocupando el
    /// índice único de responsabilidad para siempre y contaminando la detección
    /// de conflictos. Restaurar el objetivo no la reabre: eso es un acto
    /// explícito.
    /// </summary>
    ObjetivoEliminado = 4,

    /// <summary>
    /// Cerrada por el backfill de F1 al migrar una delegación que ya estaba
    /// inactiva. El modelo antiguo no guardaba la fecha de desactivación, así
    /// que la vigencia real de esa etapa es desconocida — se marca para no
    /// fingir un dato que nunca existió.
    /// </summary>
    MigradaSinFecha = 5
}
