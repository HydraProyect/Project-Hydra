using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using MediatR;

namespace CaeManager.Application.Tenants.Commands.RetirarEncargoAdministracion;

/// <summary>
/// Retira un Encargo de administración del Tenant activo (decisión D-8,
/// 2026-10-08). Misma autoridad que registrarlo
/// (<see cref="AutoridadSobreElEncargo"/>): un Administrador propio del Tenant
/// propietario o Soporte TALVEG en una Sesión Privilegiada de aprovisionamiento
/// sobre ese Tenant; <b>nunca el Operador CAE que lo recibe</b>, ni siquiera su
/// Administrador con el rol elevado por este mismo encargo.
///
/// <para>
/// El efecto es inmediato en Application —el rol efectivo se recalcula en cada
/// llamada y vuelve al de la cartera— y, para un circuito de Blazor ya abierto
/// con el rol elevado, en la siguiente revalidación del circuito. La cartera y
/// la operación no se tocan: retirar el encargo baja el techo, no quita acceso.
/// </para>
/// </summary>
public record RetirarEncargoAdministracionCommand(Guid EncargoId) : ICommand, IComandoDeAprovisionamiento;

public class RetirarEncargoAdministracionCommandHandler(
    ITenantActual tenantActual,
    AutoridadSobreElEncargo autoridad,
    IEncargoAdministracionRepository encargos,
    TimeProvider reloj)
    : IRequestHandler<RetirarEncargoAdministracionCommand, Result>
{
    public async Task<Result> Handle(RetirarEncargoAdministracionCommand request, CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } propietarioTenantId
            || await autoridad.ResolverAsync(propietarioTenantId, cancellationToken) is not { } quienRetira)
            return Result.Fallo(AutoridadSobreElEncargo.NoAutorizado);

        var encargo = await encargos.ObtenerPorIdAsync(request.EncargoId, propietarioTenantId, cancellationToken);
        if (encargo is null)
            return Result.Fallo(ErroresEncargoAdministracion.NoEncontrado);

        if (await autoridad.EsDelOperadorCaeAsync(encargo.OperadorTenantId))
            return Result.Fallo(AutoridadSobreElEncargo.NoAutorizado);

        if (encargo.RetiradoEnUtc is not null)
            return Result.Fallo(ErroresEncargoAdministracion.YaRetirado);

        encargo.Retirar(quienRetira.ActorRealUsuarioId, reloj.GetUtcNow().UtcDateTime);

        // Dos retiradas a la vez: gana una, y la otra encuentra el encargo ya retirado.
        return await encargos.GuardarDetectandoCarreraAsync(cancellationToken)
            ? Result.Exito()
            : Result.Fallo(ErroresEncargoAdministracion.YaRetirado);
    }
}
