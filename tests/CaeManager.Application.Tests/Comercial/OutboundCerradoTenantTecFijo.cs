using CaeManager.Application.Comercial.LineaOutbound;

namespace CaeManager.Application.Tests.Comercial;

/// <summary>
/// Respuesta fija de <see cref="IOutboundCerradoTenantTec"/> para los tests de handlers y behaviors con la guarda de D3;
/// registra por qué Tenants beneficiarios se preguntó.
/// </summary>
public sealed class OutboundCerradoTenantTecFijo(Func<Guid, bool> cerrado) : IOutboundCerradoTenantTec
{
    public static OutboundCerradoTenantTecFijo Abierto() => new(_ => false);

    public static OutboundCerradoTenantTecFijo Cerrado() => new(_ => true);

    public List<Guid> Preguntados { get; } = [];

    public Task<bool> EstaCerradoAsync(Guid tenantBeneficiarioId, CancellationToken cancellationToken = default)
    {
        Preguntados.Add(tenantBeneficiarioId);
        return Task.FromResult(cerrado(tenantBeneficiarioId));
    }
}
