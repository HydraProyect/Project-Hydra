using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Queries.ObtenerTenantObjetivoSesion;

/// <summary>
/// El Tenant objetivo de la Sesión Privilegiada de acceso total en curso (el Tenant actual), con la forma de
/// <see cref="ClienteAutorizadoDto"/> para los lectores que recorren Tenants. Null si no hay sesión de acceso total
/// sobre el Tenant actual (<see cref="IPoliticaNotaInterna.TieneAccesoTotalAlTenantAsync"/>): fallo cerrado.
/// No es Tenant de origen del Actor de Plataforma TALVEG, así que <c>EsOrigen</c> es false.
/// </summary>
public record ObtenerTenantObjetivoSesionQuery : IRequest<ClienteAutorizadoDto?>;

public class ObtenerTenantObjetivoSesionQueryHandler(
    IPoliticaNotaInterna politicaAcceso,
    ITenantActual tenantActual,
    ITenantsQueryContext tenantsContext)
    : IRequestHandler<ObtenerTenantObjetivoSesionQuery, ClienteAutorizadoDto?>
{
    public async Task<ClienteAutorizadoDto?> Handle(ObtenerTenantObjetivoSesionQuery request, CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } tenantObjetivoId
            || !await politicaAcceso.TieneAccesoTotalAlTenantAsync(cancellationToken))
            return null;

        var tenant = await tenantsContext.Tenants
            .Where(t => t.Id == tenantObjetivoId)
            .Select(t => new { t.Id, t.Nombre, t.LogoVersion })
            .FirstOrDefaultAsync(cancellationToken);

        return tenant is null ? null : new ClienteAutorizadoDto(tenant.Id, tenant.Nombre, EsOrigen: false, LogoVersion: tenant.LogoVersion);
    }
}
