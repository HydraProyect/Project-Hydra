using CaeManager.Application.ApiKeys.Queries.ObtenerClavesApi;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.ApiKeys;
using MediatR;

namespace CaeManager.Application.ApiKeys.Queries.ObtenerClavesApiDeMiOrganizacion;

/// <summary>
/// Las claves de la API pública del Tenant propietario actual, para que su Administrador
/// (<see cref="AutoridadClavesApiDelTenant"/>) sepa cuáles hay y pueda revocarlas. Quien no tiene esa
/// autoridad recibe una lista vacía. El prefijo visible y los demás datos son los del listado de
/// plataforma; el hash nunca sale.
/// </summary>
public record ObtenerClavesApiDeMiOrganizacionQuery : IRequest<IReadOnlyList<ClaveApiDto>>;

public class ObtenerClavesApiDeMiOrganizacionQueryHandler(
    IApiKeysQueryContext claveContext,
    IClaveApiRepository claveRepositorio,
    ITenantActual tenantActual,
    ICurrentUserService currentUserService,
    IAdministradorDelTenantPropietario administradorDelTenant)
    : IRequestHandler<ObtenerClavesApiDeMiOrganizacionQuery, IReadOnlyList<ClaveApiDto>>
{
    public async Task<IReadOnlyList<ClaveApiDto>> Handle(ObtenerClavesApiDeMiOrganizacionQuery request, CancellationToken cancellationToken)
    {
        if (!await AutoridadClavesApiDelTenant.PuedeGestionarAsync(tenantActual, currentUserService, administradorDelTenant, cancellationToken))
            return [];

        return await ListadoClavesApi.LeerAsync(claveContext, claveRepositorio, tenantActual.TenantId!.Value, cancellationToken);
    }
}
