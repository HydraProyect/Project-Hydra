using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.AlertasCriticas;

public record AlertaCriticaDto(string Texto, EstadoAlertaCritica Estado, bool PuedeResolver, bool? SegundaOpinionIa, int? ConfianzaIa);
public record ObtenerAlertaCriticaQuery(Guid ConversacionId) : IRequest<AlertaCriticaDto?>;
public record ResolverAlertaCriticaCommand(Guid ConversacionId, bool ConfirmarYDerivar) : ICommand;

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
        var alerta = await repositorio.ObtenerPorConversacionAsync(request.ConversacionId, cancellationToken);
        return alerta is null ? null : new(alerta.TextoDisparador, alerta.Estado,
            alerta.Estado == EstadoAlertaCritica.Pendiente && await acceso.PersonaQuePuedeResolverAsync(cancellationToken) is not null,
            alerta.SegundaOpinionIa, alerta.ConfianzaIa);
    }
}

public sealed class ResolverAlertaCriticaCommandHandler(
    IAlertaCriticaRepository repositorio, AccesoAlertaCritica acceso,
    AlertaCriticaService avisos, IUnitOfWork unitOfWork) : IRequestHandler<ResolverAlertaCriticaCommand, Result>
{
    public async Task<Result> Handle(ResolverAlertaCriticaCommand request, CancellationToken cancellationToken)
    {
        if (await acceso.PersonaQuePuedeResolverAsync(cancellationToken) is not { } persona
            || !await acceso.PuedeLeerAsync(request.ConversacionId, cancellationToken))
            return Result.Fallo(Error.Crear("AlertaCritica.SinPermiso", "Solo la Dirección CAE o el Coordinador CAE con acceso a la conversación pueden resolver la alerta."));
        var alerta = await repositorio.ObtenerPorConversacionAsync(request.ConversacionId, cancellationToken);
        if (alerta is null || alerta.Estado != EstadoAlertaCritica.Pendiente)
            return Result.Fallo(Error.Crear("AlertaCritica.NoPendiente", "La alerta ya está resuelta o no está disponible."));
        alerta.Resolver(request.ConfirmarYDerivar, persona, DateTime.UtcNow);
        if (request.ConfirmarYDerivar) await avisos.AvisarAsync(alerta, true, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
