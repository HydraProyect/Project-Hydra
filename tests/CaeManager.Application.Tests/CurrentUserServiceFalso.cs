using CaeManager.Application.Common;

namespace CaeManager.Application.Tests;

public class CurrentUserServiceFalso(Guid? usuarioId = null, string? rol = null) : ICurrentUserService
{
    public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(usuarioId);

    public Task<string?> ObtenerRolActualAsync() => Task.FromResult(rol);
}
