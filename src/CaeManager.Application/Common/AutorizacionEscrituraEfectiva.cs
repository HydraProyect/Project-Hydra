using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;

namespace CaeManager.Application.Common;

/// <inheritdoc cref="IAutorizacionEscrituraEfectiva" />
public class AutorizacionEscrituraEfectiva(
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    ITenantActual tenantActual)
    : IAutorizacionEscrituraEfectiva
{
    private const string RolAdministrador = "Administrador";

    public async Task<bool> EsActoDeAdministradorAsync(CancellationToken cancellationToken = default)
    {
        if (await currentUserService.ObtenerRolActualAsync() == RolAdministrador)
            return true;

        // RevalidarAsync, no ObtenerAsync: este método autoriza una escritura,
        // así que no puede confiar en una memo de circuito que pudo resolverse
        // antes de que la concesión se revocara (mismo motivo que
        // AutorizacionEscrituraBehavior, REC-067).
        var sesion = await sesionPrivilegiadaActual.RevalidarAsync(cancellationToken);

        return sesion is { Capacidad: CapacidadPrivilegio.Aprovisionamiento } s
               && s.TenantObjetivoId == tenantActual.TenantId;
    }
}
