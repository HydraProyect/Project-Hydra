using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Tenants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comercial.Queries.ObtenerEstadoComercialTenants;

/// <summary>
/// Alimenta el panel de plataforma (Christopher, hoy el único "comercial")
/// para ver de un vistazo qué tenant tiene qué suscripción y en qué estado.
/// El tenant de plataforma se excluye siempre de los resultados: nunca es él
/// mismo un suscriptor (ver TenantConfiguration.HasData).
///
/// <c>Tenant</c> no lleva filtro global por tenant (ver TenantConfiguration),
/// así que sin esta comprobación cualquier Administrador de cualquier tenant
/// cliente podría ver el estado comercial de TODOS los demás tenants — la
/// misma fuga que <c>GenerarClaveApiCommandHandler</c> evita en escritura.
/// Aquí no hay un pipeline behavior genérico que lo cubra (a diferencia de
/// AutorizacionEscrituraBehavior/GateComercialTenantBehavior, que solo miran
/// Commands): la comprobación va en el propio handler, fallando cerrado
/// (lista vacía) igual que <c>EsAdministradorPlataformaQueryHandler</c>.
/// </summary>
public record ObtenerEstadoComercialTenantsQuery : IRequest<IReadOnlyList<EstadoComercialTenantDto>>;

public record EstadoComercialTenantDto(
    Guid TenantId,
    string Nombre,
    EstadoComercialTenant EstadoComercial,
    string? StripeCustomerId,
    string? StripeSubscriptionId,
    DateTime? EstadoComercialActualizadoEnUtc);

public class ObtenerEstadoComercialTenantsQueryHandler(ITenantsQueryContext dbContext, ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerEstadoComercialTenantsQuery, IReadOnlyList<EstadoComercialTenantDto>>
{
    public async Task<IReadOnlyList<EstadoComercialTenantDto>> Handle(
        ObtenerEstadoComercialTenantsQuery request, CancellationToken cancellationToken)
    {
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        var esPlataforma = tenantOrigenId is not null && await dbContext.Tenants
            .AnyAsync(t => t.Id == tenantOrigenId.Value && t.EsPlataforma, cancellationToken);

        if (!esPlataforma)
            return [];

        return await dbContext.Tenants
            .Where(t => !t.EsPlataforma)
            .OrderBy(t => t.Nombre)
            .Select(t => new EstadoComercialTenantDto(
                t.Id, t.Nombre, t.EstadoComercial, t.StripeCustomerId, t.StripeSubscriptionId, t.EstadoComercialActualizadoEnUtc))
            .ToListAsync(cancellationToken);
    }
}
