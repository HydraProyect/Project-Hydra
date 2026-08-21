using CaeManager.Application.Tenants;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// Doble de <see cref="IAutorizacionDelegacionTenant"/>. Los tests de invariante
/// de dominio le piden que autorice, porque lo que ejercitan es lo que ocurre
/// DESPUÉS de la autorización; los tests de autorización usan el propio handler
/// con este doble diciendo que no.
/// </summary>
public class AutorizacionDelegacionFalsa(bool autoriza) : IAutorizacionDelegacionTenant
{
    public Guid? UltimoTenantConsultado { get; private set; }

    public Task<bool> PuedeGestionarDelegacionesAsync(
        Guid usuarioId, Guid tenantClienteDeleganteId, CancellationToken cancellationToken = default)
    {
        UltimoTenantConsultado = tenantClienteDeleganteId;
        return Task.FromResult(autoriza);
    }
}
