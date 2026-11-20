using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Common;

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

    /// <summary>Cada pregunta con el Tenant activo explícito (<see cref="AmbitoTenantExplicito"/>) con que se hizo.</summary>
    public List<(Guid Preguntado, Guid? Ambito)> Ambitos { get; } = [];

    public Task<bool> EstaCerradoAsync(Guid tenantBeneficiarioId, CancellationToken cancellationToken = default)
    {
        Preguntados.Add(tenantBeneficiarioId);
        Ambitos.Add((tenantBeneficiarioId, AmbitoTenantExplicito.TenantIdActual));
        return Task.FromResult(cerrado(tenantBeneficiarioId));
    }
}
