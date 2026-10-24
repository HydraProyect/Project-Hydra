using CaeManager.Application.Plataforma;

namespace CaeManager.IntegrationTests.ApiKeys;

internal sealed class AutorizacionClavesApiDePrueba(Guid tenantObjetivoId) : IAutorizacionAdminPlataforma
{
    public Task<bool> PuedeSobreTenantAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(tenantId == tenantObjetivoId);

    public Task<bool> PuedeGlobalmenteAsync(Guid usuarioId, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
