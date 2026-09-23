using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;

namespace CaeManager.Application.Auditoria;

/// <inheritdoc cref="IRegistroAccesoDatoSensibleService"/>
public class RegistroAccesoDatoSensibleService(
    IActorAuditoria actorAuditoria,
    IRegistroAccesoDatoSensibleRepository repositorio)
    : IRegistroAccesoDatoSensibleService
{
    public async Task RegistrarAsync(string entidadTipo, Guid entidadId, CancellationToken cancellationToken = default)
    {
        var actor = await actorAuditoria.ObtenerAsync();

        // Mismo criterio dual que AuditoriaInterceptor: UsuarioId es quien
        // figura como autor (el simulado durante una impersonación) y
        // ActorRealUsuarioId quien estaba realmente detrás del teclado.
        var registro = new RegistroAuditoria(
            entidadTipo,
            entidadId,
            RegistroAuditoria.AccionAccesoDatoSensible,
            datosAntes: null,
            datosDespues: null,
            usuarioId: actor.UsuarioSimuladoId ?? actor.ActorRealUsuarioId,
            tipoActor: (TipoActorAuditoria)actor.ResolverTipoActor(),
            actorRealUsuarioId: actor.ActorRealUsuarioId,
            viaAcceso: (TipoViaAccesoAuditoria)actor.Via,
            viaAccesoId: actor.ViaAccesoId);

        await repositorio.GuardarAsync(registro, cancellationToken);
    }
}
