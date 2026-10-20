using CaeManager.Application.Common;

namespace CaeManager.Application.Visitas;

/// <summary>
/// Quién firma la asignación de un Trabajador a una Visita (<c>VisitaTrabajador.GestionadoPorUsuarioId</c>): la persona
/// que estaba detrás del teclado (<see cref="ActorAuditoria.ActorRealUsuarioId"/>), no el Usuario simulado de una
/// impersonación ni el principal de la Asignación de Cartera. Es autoría, así que sale de <see cref="IActorAuditoria"/>
/// y no de <see cref="ICurrentUserService"/>. Sin identidad resuelta (procesos de fondo, siembras), <c>null</c>.
/// </summary>
public static class GestorDeLaAsignacion
{
    public static async Task<Guid?> ActorRealAsync(IActorAuditoria? actorAuditoria) =>
        actorAuditoria is null ? null : (await actorAuditoria.ObtenerAsync()).ActorRealUsuarioId;
}
