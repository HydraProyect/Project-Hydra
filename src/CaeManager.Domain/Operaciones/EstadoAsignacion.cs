namespace CaeManager.Domain.Operaciones;

/// <summary>
/// Estado de una asignación de responsabilidad operativa (de operación o de
/// cartera) — ver ADR-011 § 2.7.
///
/// Las asignaciones son <b>append-only</b>: nunca se edita el operador, el
/// ámbito ni el servicio de una fila. Todo cambio es cerrar una y abrir otra,
/// que es lo que permite responder "¿quién era responsable de este ámbito el 15
/// de marzo?" sin reconstruir eventos.
/// </summary>
public enum EstadoAsignacion
{
    /// <summary>
    /// Concedida pero todavía no responsable: su <c>VigenciaDesde</c> está en el
    /// futuro. Concede <b>solo lectura</b> desde que se crea, para que el
    /// operador entrante pueda ver lo que va a heredar durante un traspaso
    /// (ADR-011 § 4.5). No ocupa los índices únicos de responsabilidad.
    /// </summary>
    Programada = 0,

    /// <summary>Responsable efectivo del ámbito.</summary>
    Vigente = 1,

    /// <summary>
    /// Suspendida temporalmente sin cerrarse: conserva su lugar y su historia,
    /// pero no responde del ámbito ni ocupa los índices únicos.
    /// </summary>
    Suspendida = 2,

    /// <summary>
    /// Terminada. Estado final: una asignación cerrada nunca se reabre — se
    /// abre otra. Conserva el rastro de quién operó qué y cuándo.
    /// </summary>
    Cerrada = 3
}
