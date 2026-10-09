using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;

namespace CaeManager.Application.Auditoria;

/// <inheritdoc cref="IRegistroExportacionService"/>
public class RegistroExportacionService(
    IActorAuditoria actorAuditoria,
    IRegistroAccesoDatoSensibleRepository repositorio)
    : IRegistroExportacionService
{
    public async Task RegistrarAsync(
        string entidadTipo, int filas, IReadOnlyDictionary<string, string> criterios,
        CancellationToken cancellationToken = default)
    {
        var actor = await actorAuditoria.ObtenerAsync();

        // Mismo criterio dual que RegistroAccesoDatoSensibleService: UsuarioId es
        // quien figura como autor (el simulado durante una impersonación) y
        // ActorRealUsuarioId quien estaba realmente detrás del teclado.
        var registro = new RegistroAuditoria(
            entidadTipo,
            Guid.Empty,
            RegistroAuditoria.AccionExportacion,
            datosAntes: null,
            datosDespues: JsonSerializer.Serialize(new { filas, criterios }),
            usuarioId: actor.UsuarioSimuladoId ?? actor.ActorRealUsuarioId,
            tipoActor: (TipoActorAuditoria)actor.ResolverTipoActor(),
            actorRealUsuarioId: actor.ActorRealUsuarioId,
            viaAcceso: (TipoViaAccesoAuditoria)actor.Via,
            viaAccesoId: actor.ViaAccesoId);

        await repositorio.GuardarAsync(registro, cancellationToken);
    }
}
