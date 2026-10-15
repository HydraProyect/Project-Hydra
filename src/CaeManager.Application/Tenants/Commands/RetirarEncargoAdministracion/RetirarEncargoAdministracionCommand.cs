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
/// sobre ese Tenant. Además, desde el 2026-10-15 (decisión de Chris), el
/// Administrador del Operador CAE externo que administra el Tenant por ESTE
/// encargo, si es pleno (Tenant propietario sin Administrador propio, ni activo ni pendiente de activar),
/// puede renunciar a él (<see cref="AutoridadSobreElEncargo.ResolverRenunciaAsync"/>).
/// Con encargo no pleno, el Operador CAE sigue sin poder retirarlo.
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
        if (tenantActual.TenantId is not { } propietarioTenantId)
            return Result.Fallo(AutoridadSobreElEncargo.NoAutorizado);

        // Dos vías, y no se mezclan: la Propiedad (plataforma o Administrador propio), que nunca es
        // del Operador CAE; o la renuncia del Operador CAE que administra por ESTE encargo pleno.
        Guid actorQueRetira;
        var porRenuncia = false;
        if (await autoridad.ResolverAsync(propietarioTenantId, cancellationToken) is { } quienRetira)
            actorQueRetira = quienRetira.ActorRealUsuarioId;
        else if (await autoridad.ResolverRenunciaAsync(request.EncargoId, cancellationToken) is { } quienRenuncia)
            (actorQueRetira, porRenuncia) = (quienRenuncia, true);
        else
            return Result.Fallo(AutoridadSobreElEncargo.NoAutorizado);

        var encargo = await encargos.ObtenerPorIdAsync(request.EncargoId, propietarioTenantId, cancellationToken);
        if (encargo is null)
            return Result.Fallo(ErroresEncargoAdministracion.NoEncontrado);

        // Por la vía de la Propiedad, nadie del Operador CAE. La renuncia ya exige actuar por este encargo.
        if (!porRenuncia && await autoridad.EsDelOperadorCaeAsync(encargo.OperadorTenantId))
            return Result.Fallo(AutoridadSobreElEncargo.NoAutorizado);

        if (encargo.RetiradoEnUtc is not null)
            return Result.Fallo(ErroresEncargoAdministracion.YaRetirado);

        encargo.Retirar(actorQueRetira, reloj.GetUtcNow().UtcDateTime);

        // Dos retiradas a la vez: gana una, y la otra encuentra el encargo ya retirado.
        return await encargos.GuardarDetectandoCarreraAsync(cancellationToken)
            ? Result.Exito()
            : Result.Fallo(ErroresEncargoAdministracion.YaRetirado);
    }
}
