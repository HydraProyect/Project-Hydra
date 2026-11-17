using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Auditoria;

/// <summary>
/// Auditoría del cambio de contexto con éxito y del comando rechazado por pestaña obsoleta (D-6 b, R-2; propuesta de
/// buzones de varios Tenants § 5.5).
/// </summary>
public interface IRegistroCambioContexto
{
    /// <summary>
    /// El cambio de contexto a <paramref name="tenantDestinoId"/>, ya autorizado, en el <c>RegistroAuditoria</c>
    /// de ese Tenant y solo en él. Se llama antes de emitir la cookie: si no queda registrado, no hay cambio.
    /// </summary>
    Task RegistrarCambioAsync(
        Guid tenantDestinoId, Guid tenantOrigenActorId, Guid? asignacionOperacionId, string origen,
        CancellationToken cancellationToken);

    /// <summary>
    /// Un comando que no se ejecutó porque la selección del ámbito ya no era la vigente, en el Tenant en el que
    /// corre ese ámbito. Mejor esfuerzo: si el registro falla, el comando sigue rechazado y queda la traza.
    /// </summary>
    Task RegistrarRechazoAsync(string comando, SeleccionNoVigente motivo, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRegistroCambioContexto"/>
public class RegistroCambioContextoService(
    IActorAuditoria actorAuditoria,
    IRegistroAccesoDatoSensibleRepository repositorio,
    ILogger<RegistroCambioContextoService> logger)
    : IRegistroCambioContexto
{
    public const string EntidadTipo = "ContextoTenant";

    /// <summary>Origen del cambio hecho con el selector de la cabecera (propuesta § 5.5, «Cambio manual con éxito»).</summary>
    public const string OrigenSelector = "Selector";

    public async Task RegistrarCambioAsync(
        Guid tenantDestinoId, Guid tenantOrigenActorId, Guid? asignacionOperacionId, string origen,
        CancellationToken cancellationToken)
    {
        var actor = await actorAuditoria.ObtenerAsync();

        // Solo el Tenant de destino, y sin el Tenant seleccionado antes: diría a los auditores de esta Empresa
        // gestionada para qué otras trabaja esa persona (propuesta § 5.5). El Tenant de origen del actor sí consta:
        // es su Operador CAE, el mismo que ya nombra la Asignación de Operación.
        var registro = new RegistroAuditoria(
            EntidadTipo,
            tenantDestinoId,
            RegistroAuditoria.AccionCambioDeContexto,
            datosAntes: null,
            datosDespues: JsonSerializer.Serialize(new { origen, tenantOrigenActor = tenantOrigenActorId }),
            // Mismo criterio dual que RegistroExportacionService: quien figura como autor (el simulado en una
            // impersonación) y quien estaba de verdad detrás del teclado, por separado.
            usuarioId: actor.UsuarioSimuladoId ?? actor.ActorRealUsuarioId,
            tipoActor: (TipoActorAuditoria)actor.ResolverTipoActor(),
            actorRealUsuarioId: actor.ActorRealUsuarioId,
            // La vía es la de la selección nueva, no la de la que se deja.
            viaAcceso: asignacionOperacionId is null ? TipoViaAccesoAuditoria.Normal : TipoViaAccesoAuditoria.OperacionDelegada,
            viaAccesoId: asignacionOperacionId);

        // El destino ya está autorizado por quien llama; el ámbito explícito hace que el interceptor de RLS
        // realinee el contexto a ese Tenant antes del SaveChanges.
        using (AmbitoTenantExplicito.Establecer(tenantDestinoId))
            await repositorio.GuardarAsync(registro, cancellationToken);
    }

    public async Task RegistrarRechazoAsync(string comando, SeleccionNoVigente motivo, CancellationToken cancellationToken)
    {
        try
        {
            var actor = await actorAuditoria.ObtenerAsync();
            await repositorio.GuardarAsync(new RegistroAuditoria(
                EntidadTipo,
                Guid.Empty,
                RegistroAuditoria.AccionComandoRechazadoPorContextoObsoleto,
                datosAntes: null,
                datosDespues: JsonSerializer.Serialize(new { comando, motivo = motivo.ToString() }),
                usuarioId: actor.UsuarioSimuladoId ?? actor.ActorRealUsuarioId,
                tipoActor: (TipoActorAuditoria)actor.ResolverTipoActor(),
                actorRealUsuarioId: actor.ActorRealUsuarioId,
                viaAcceso: (TipoViaAccesoAuditoria)actor.Via,
                viaAccesoId: actor.ViaAccesoId), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "No se pudo registrar el rechazo por pestaña obsoleta de {Comando} ({Motivo}).", comando, motivo);
        }
    }
}
