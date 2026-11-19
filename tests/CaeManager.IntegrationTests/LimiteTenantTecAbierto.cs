using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Common;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Dobles para los tests que construyen a mano un handler con la guarda de D3 y no prueban esa guarda: un Tenant activo
/// fijo y Outbound abierto. La guarda se prueba en <c>LimiteTenantTecBehaviorTests</c>, en
/// <c>EditarEmpresaLimiteTenantTecTests</c> y, bajo PostgreSQL, en <c>LimiteTenantTecBajoRuntimeTests</c>.
/// </summary>
public sealed class OutboundTenantTecAbierto : IOutboundCerradoTenantTec
{
    public static readonly OutboundTenantTecAbierto Instancia = new();

    public Task<bool> EstaCerradoAsync(Guid tenantBeneficiarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

public sealed class TenantActualDeLimiteTenantTec(Guid tenantId) : ITenantActual
{
    public Guid? TenantId { get; } = tenantId;
}
