using CaeManager.Application.Common;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;

namespace CaeManager.Application.AsistenteIa.Tareas;

/// <summary>
/// Quién trabaja con el asistente y dónde, resuelto y comprobado antes de tocar
/// una <see cref="TareaAsistente"/>.
/// </summary>
/// <param name="ActorRealUsuarioId">La persona: el Actor real, nunca el Usuario simulado.</param>
/// <param name="UsuarioSimuladoId">A quién simula, si simula a alguien. Hoy siempre <c>null</c>.</param>
/// <param name="TenantObjetivoId">El Tenant en el que se trabaja (el que se sella en la tarea).</param>
/// <param name="TenantOrigenId">El Tenant al que pertenece la cuenta de la persona.</param>
/// <param name="Via">Cómo opera el Tenant objetivo.</param>
/// <param name="ViaAccesoId">La Asignación de Operación, si la vía es la delegada.</param>
public sealed record PersonaTareaAsistente(
    Guid ActorRealUsuarioId,
    Guid? UsuarioSimuladoId,
    Guid TenantObjetivoId,
    Guid? TenantOrigenId,
    TipoViaAccesoAuditoria Via,
    Guid? ViaAccesoId);

/// <summary>
/// Las comprobaciones explícitas de multi-tenancy de toda operación sobre
/// tareas del asistente. Una coordenada de contexto no es autoridad: el Tenant
/// objetivo solo vale si llega por una vía que lo justifica.
/// <list type="number">
/// <item><b>Usuario</b>: hay un Actor real resuelto.</item>
/// <item><b>Identidad efectiva de PostgreSQL</b>: <c>app.usuario_id</c>, contra
/// el que compara la política <c>solo_su_persona</c>, sale de
/// <see cref="ICurrentUserService.ObtenerUsuarioActualIdAsync"/>; tiene que ser
/// el mismo Actor real, o la base filtraría por otra persona que la que la
/// aplicación cree.</item>
/// <item><b>Plataforma</b>: una Sesión Privilegiada no usa el asistente —Soporte
/// TALVEG nunca es Gestor CAE—, y una vía sin resolver tampoco.</item>
/// <item><b>Tenant objetivo</b>: hay uno resuelto.</item>
/// <item><b>Tenant de origen</b>: si el objetivo es otro Tenant, solo vale la
/// operación delegada (el Gestor CAE de un Operador CAE externo en el Workspace
/// operativo derivado del Tenant beneficiario, con su Asignación de
/// Operación). La delegación heredada de soporte selecciona otro Tenant con vía
/// normal, y queda fuera.</item>
/// </list>
/// El alcance de cartera no se comprueba aquí porque la tarea no contiene ni
/// concede datos del dominio: es una conversación y un plan. Lo comprueban la
/// resolución de candidatos y los Commands que ejecutan cada paso.
/// </summary>
public static class ResolucionPersonaTareaAsistente
{
    public static async Task<Result<PersonaTareaAsistente>> ResolverAsync(
        IActorAuditoria actorAuditoria,
        ICurrentUserService currentUserService,
        ITenantActual tenantActual)
    {
        var actor = await actorAuditoria.ObtenerAsync();
        if (actor.ActorRealUsuarioId is not { } actorReal)
            return Fallo("TareaAsistente.SinPersona", "No pudimos identificar a la persona que usa el asistente.");

        if (await currentUserService.ObtenerUsuarioActualIdAsync() != actorReal)
            return Fallo("TareaAsistente.IdentidadIncoherente", "La sesión no identifica a la misma persona que la auditoría.");

        var via = (TipoViaAccesoAuditoria)actor.Via;
        if (via == TipoViaAccesoAuditoria.SesionPrivilegiada)
            return Fallo("TareaAsistente.SesionPrivilegiada", "El asistente no está disponible en una sesión de soporte.");
        if (via is not (TipoViaAccesoAuditoria.Normal or TipoViaAccesoAuditoria.OperacionDelegada))
            return Fallo("TareaAsistente.ViaDesconocida", "No pudimos determinar cómo accedes a esta empresa.");
        if (via == TipoViaAccesoAuditoria.OperacionDelegada && actor.ViaAccesoId is null)
            return Fallo("TareaAsistente.ViaDesconocida", "No pudimos determinar cómo accedes a esta empresa.");

        if (tenantActual.TenantId is not { } tenantObjetivo)
            return Fallo("TareaAsistente.SinTenant", "No hay ninguna empresa seleccionada.");

        var tenantOrigen = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (tenantOrigen != tenantObjetivo && via != TipoViaAccesoAuditoria.OperacionDelegada)
            return Fallo("TareaAsistente.TenantSinOperacion", "El asistente solo trabaja en otra empresa a través de una operación asignada.");

        return Result.Exito(new PersonaTareaAsistente(
            actorReal,
            actor.UsuarioSimuladoId,
            tenantObjetivo,
            tenantOrigen,
            via,
            via == TipoViaAccesoAuditoria.OperacionDelegada ? actor.ViaAccesoId : null));
    }

    private static Result<PersonaTareaAsistente> Fallo(string codigo, string mensaje) =>
        Result.Fallo<PersonaTareaAsistente>(Error.Crear(codigo, mensaje));

    internal static Error NoEncontrada() =>
        Error.Crear("TareaAsistente.NoEncontrada", "No encontramos esta tarea del asistente.");

    internal static Error TransicionNoValida(string mensaje) =>
        Error.Crear("TareaAsistente.TransicionNoValida", mensaje);
}
