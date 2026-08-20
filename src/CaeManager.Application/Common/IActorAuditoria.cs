namespace CaeManager.Application.Common;

/// <summary>
/// Resuelve la identidad de auditoría — <b>irrenunciable</b>, a diferencia de
/// <see cref="ICurrentUserService"/>, que resuelve la identidad de
/// autorización y sí será simulable cuando exista la impersonación.
///
/// Son dos contratos separados a propósito. Todo lo que firme autoría
/// (registros de auditoría, <c>EliminadoPorUsuarioId</c>, aprobaciones,
/// <c>CreadoPorUsuarioId</c>) debe depender de este; todo lo que decida qué se
/// puede ver o hacer depende del otro. Mientras no exista la impersonación
/// ambos devuelven el mismo usuario y el comportamiento no cambia: la
/// separación es lo que hace que el día que exista no haya que revisar cada
/// campo del sistema para saber cuál de las dos identidades le tocaba.
/// </summary>
public interface IActorAuditoria
{
    Task<ActorAuditoria> ObtenerAsync();

    /// <summary>
    /// Versión no bloqueante para el guardado síncrono. Devuelve
    /// <c>null</c> si la identidad no está ya resuelta, en vez de esperar:
    /// bloquear sobre un <c>Task</c> pendiente dentro de un circuito de Blazor
    /// arriesga un interbloqueo. Quien la use debe tratar el <c>null</c> como
    /// <see cref="ActorAuditoria.SinResolver"/>, nunca como "usuario anónimo
    /// en vía normal".
    /// </summary>
    ActorAuditoria? ObtenerSiYaEstaResuelto();
}
