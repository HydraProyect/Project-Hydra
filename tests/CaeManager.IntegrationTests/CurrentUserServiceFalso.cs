using CaeManager.Application.Common;

namespace CaeManager.IntegrationTests;

public class CurrentUserServiceFalso(Guid? usuarioId = null, string? rol = null, Guid? tenantOrigenId = null) : ICurrentUserService
{
    public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(usuarioId);

    public Task<string?> ObtenerRolActualAsync() => Task.FromResult(rol);

    public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(tenantOrigenId);
}
