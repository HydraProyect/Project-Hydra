using CaeManager.Application.Common;

namespace CaeManager.Application.Tests;

public class CurrentUserServiceFalso(
    Guid? usuarioId = null, string? rol = null, Guid? tenantOrigenId = null, bool tieneDobleFactorActivo = true)
    : ICurrentUserService
{
    public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(usuarioId);

    public Task<string?> ObtenerRolActualAsync() => Task.FromResult(rol);

    public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(tenantOrigenId);

    public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(tieneDobleFactorActivo);
}
