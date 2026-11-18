using CaeManager.Application.Common;

namespace CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;

/// <summary>
/// D-12.2 en los dos caminos que dan una evidencia Inbound por buena (<see cref="RegistrarEvidenciaInboundCommand"/> sin
/// rechazo y <see cref="ValidarEvidenciaInboundCommand"/>): nadie valida la versión de un Documento que subió él mismo,
/// comparando tanto el usuario efectivo como el Actor real de quien valida con los dos autores de la versión.
/// <para>
/// <b>Provisional.</b> La marca <see cref="LecturaInbound.DocumentoInboundDto.SubidoPorQuienLee"/> la calcula
/// <c>app_inbound_documentos_autorizados</c> con <c>app_ctx_usuario_id()</c>, que es el usuario efectivo que firma
/// <c>TenantRlsConnectionInterceptor</c>; el Actor real de quien valida no viaja en el contexto firmado. Sin impersonación los
/// dos coinciden y la comparación de la función cubre D-12.2 entera. Con impersonación (X actúa como Y y valida lo que subió
/// X) la función solo vería a Y y la marca saldría falsa, así que aquí se falla cerrado: bajo impersonación Validar no está
/// disponible y Rechazar sí. Hoy no hay impersonación (<c>ActorAuditoriaDesdeSesion</c> deja el Usuario simulado siempre
/// nulo) y una Sesión Privilegiada no escribe (<c>AutorizacionEscrituraBehavior</c>), así que la guarda no cambia ningún
/// comportamiento vigente. Se retira cuando el contexto firmado lleve también el Actor real (por ejemplo
/// <c>app.actor_real_id</c>) y la función compare los dos; ese cambio es de Seguridad y plataforma, no de Inbound.
/// </para>
/// </summary>
internal static class GuardaD12
{
    public static async Task<bool> HayImpersonacionAsync(IActorAuditoria actorAuditoria)
    {
        var actor = await actorAuditoria.ObtenerAsync();
        return actor.UsuarioSimuladoId is { } simulado && simulado != actor.ActorRealUsuarioId;
    }
}
