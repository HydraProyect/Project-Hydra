using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Incidencias;
using CaeManager.Domain.Notificaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.AlertasCriticas;

public record AlertaCriticaDto(Guid Id, string Texto, EstadoAlertaCritica Estado, bool PuedeResolver, bool? SegundaOpinionIa, int? ConfianzaIa);
/// <summary>Devuelve la alerta Pendiente de la conversación o, si no hay, la más reciente ya resuelta.</summary>
public record ObtenerAlertaCriticaQuery(Guid ConversacionId) : IRequest<AlertaCriticaDto?>;
/// <summary>Resuelve la alerta que se mostró (AlertaId), no «la de la conversación»: en un hilo puede haber varias a lo largo del tiempo.</summary>
public record ResolverAlertaCriticaCommand(Guid ConversacionId, Guid AlertaId, bool ConfirmarYDerivar, Guid? IncidenciaId = null) : ICommand;
/// <summary>«Deshacer» tras «Registrar incidencia»: elimina la Incidencia, devuelve la alerta a Pendiente y retira los avisos «derivado», todo en una transacción.</summary>
public record DeshacerDerivacionAlertaCriticaCommand(Guid ConversacionId, Guid AlertaId, Guid IncidenciaId) : ICommand;

public sealed class AccesoAlertaCritica(
    ICurrentUserService usuario,
    ISesionPrivilegiadaActual sesion,
    ITenantActual tenant,
    IComunicacionesQueryContext conversaciones,
    IAlcanceDatosService alcance,
    IDirectorioUsuariosService directorio)
{
    public async Task<bool> PuedeLeerAsync(Guid conversacionId, CancellationToken ct)
    {
        if (await sesion.ObtenerAsync(ct) is not null || await usuario.ObtenerRolEfectivoAsync() is not
            ("Administrador" or "DireccionCae" or "CoordinadorCae" or "GestorCae" or "Consulta")) return false;
        var conversacion = await conversaciones.Conversaciones.Where(c => c.Id == conversacionId)
            .Select(c => new { c.ClienteId, c.EmpresaId, c.ConexionIntegracionId }).FirstOrDefaultAsync(ct);
        return conversacion is not null && await alcance.ConversacionVisibleAsync(
            conversacion.ClienteId, conversacion.EmpresaId, conversacion.ConexionIntegracionId, ct);
    }

    public async Task<Guid?> PersonaQuePuedeResolverAsync(CancellationToken ct)
    {
        if (await sesion.RevalidarAsync(ct) is not null || tenant.TenantId is not { } tenantId
            || await usuario.ObtenerTenantOrigenIdAsync() != tenantId
            || await usuario.ObtenerUsuarioActualIdAsync() is not { } persona) return null;
        var rol = await usuario.ObtenerRolEfectivoAsync();
        if (rol is not ("DireccionCae" or "CoordinadorCae")) return null;
        return await directorio.EsCuentaActivaConRolAsync(persona, tenantId, rol, ct) ? persona : null;
    }
}

public sealed class ObtenerAlertaCriticaQueryHandler(IAlertaCriticaRepository repositorio, AccesoAlertaCritica acceso)
    : IRequestHandler<ObtenerAlertaCriticaQuery, AlertaCriticaDto?>
{
    public async Task<AlertaCriticaDto?> Handle(ObtenerAlertaCriticaQuery request, CancellationToken cancellationToken)
    {
        if (!await acceso.PuedeLeerAsync(request.ConversacionId, cancellationToken)) return null;
        var alerta = await repositorio.ObtenerVigentePorConversacionAsync(request.ConversacionId, cancellationToken);
        return alerta is null ? null : new(alerta.Id, alerta.TextoDisparador, alerta.Estado,
            alerta.Estado == EstadoAlertaCritica.Pendiente && await acceso.PersonaQuePuedeResolverAsync(cancellationToken) is not null,
            alerta.SegundaOpinionIa, alerta.ConfianzaIa);
    }
}

public sealed class ResolverAlertaCriticaCommandHandler(
    IAlertaCriticaRepository repositorio, AccesoAlertaCritica acceso,
    AlertaCriticaService avisos, IIncidenciaRepository incidencias, IAlcanceDatosService alcance,
    IUnitOfWork unitOfWork) : IRequestHandler<ResolverAlertaCriticaCommand, Result>
{
    public async Task<Result> Handle(ResolverAlertaCriticaCommand request, CancellationToken cancellationToken)
    {
        if (await acceso.PersonaQuePuedeResolverAsync(cancellationToken) is not { } persona
            || !await acceso.PuedeLeerAsync(request.ConversacionId, cancellationToken))
            return Result.Fallo(Error.Crear("AlertaCritica.SinPermiso", "Solo la Dirección CAE o el Coordinador CAE con acceso a la conversación pueden resolver la alerta."));
        var alerta = await repositorio.ObtenerPorIdAsync(request.AlertaId, cancellationToken);
        if (alerta is null || alerta.ConversacionId != request.ConversacionId || alerta.Estado != EstadoAlertaCritica.Pendiente)
            return Result.Fallo(Error.Crear("AlertaCritica.NoPendiente", "La alerta ya está resuelta o no está disponible."));
        // La Incidencia que se ata debe existir en el Tenant y estar en el alcance de gestión de quien decide.
        if (request.IncidenciaId is { } incidenciaId
            && (!request.ConfirmarYDerivar || await incidencias.ObtenerPorIdAsync(incidenciaId, cancellationToken) is not { } incidencia
                || !await alcance.CentroParaGestionVisibleAsync(incidencia.CentroId, cancellationToken)))
            return Result.Fallo(Error.Crear("AlertaCritica.IncidenciaNoDisponible", "La incidencia no está disponible para esta alerta."));
        alerta.Resolver(request.ConfirmarYDerivar, persona, DateTime.UtcNow, request.IncidenciaId);
        if (request.ConfirmarYDerivar) await avisos.AvisarAsync(alerta, true, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}

/// <summary>
/// Solo quien confirmó y derivó, y solo con la Incidencia que registró. Si entre tanto llegó otra alerta Pendiente a la misma
/// conversación, no se deshace nada: devolver esta a Pendiente rompería «una Pendiente por conversación», y eliminar solo la
/// Incidencia dejaría una alerta «derivada» sin Incidencia. La Pendiente nueva ya avisó a la Dirección CAE y a los Coordinadores CAE.
/// </summary>
public sealed class DeshacerDerivacionAlertaCriticaCommandHandler(
    IAlertaCriticaRepository repositorio, AccesoAlertaCritica acceso, IIncidenciaRepository incidencias,
    IAlcanceDatosService alcance, INotificacionUsuarioRepository notificaciones, IUnitOfWork unitOfWork)
    : IRequestHandler<DeshacerDerivacionAlertaCriticaCommand, Result>
{
    public async Task<Result> Handle(DeshacerDerivacionAlertaCriticaCommand request, CancellationToken cancellationToken)
    {
        if (await acceso.PersonaQuePuedeResolverAsync(cancellationToken) is not { } persona
            || !await acceso.PuedeLeerAsync(request.ConversacionId, cancellationToken))
            return Result.Fallo(Error.Crear("AlertaCritica.SinPermiso", "Solo la Dirección CAE o el Coordinador CAE con acceso a la conversación pueden deshacer la decisión."));
        var alerta = await repositorio.ObtenerPorIdAsync(request.AlertaId, cancellationToken);
        if (alerta is null || alerta.ConversacionId != request.ConversacionId || alerta.Estado != EstadoAlertaCritica.ConfirmadaYDerivada
            || alerta.IncidenciaId != request.IncidenciaId || alerta.DecisionPorUsuarioId != persona)
            return Result.Fallo(Error.Crear("AlertaCritica.NoDeshacible", "Esta decisión ya no se puede deshacer."));
        if (await repositorio.ObtenerPendientePorConversacionAsync(request.ConversacionId, cancellationToken) is not null)
            return Result.Fallo(Error.Crear("AlertaCritica.OtraPendiente", "Hay otra alerta pendiente en esta conversación."));
        if (await incidencias.ObtenerPorIdAsync(request.IncidenciaId, cancellationToken) is not { } incidencia
            || !await alcance.CentroParaGestionVisibleAsync(incidencia.CentroId, cancellationToken))
            return Result.Fallo(Error.Crear("AlertaCritica.IncidenciaNoDisponible", "La incidencia no está disponible para esta alerta."));

        incidencia.MarcarComoEliminado(persona);
        alerta.DeshacerDerivacion(request.IncidenciaId, persona);
        // Las notificaciones no tienen estado «anulada»: marcarlas como leídas las quita del aviso emergente de quien aún no las vio.
        foreach (var aviso in await notificaciones.ObtenerPorEventoOrigenAsync(request.IncidenciaId, cancellationToken))
            aviso.MarcarComoLeida();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
