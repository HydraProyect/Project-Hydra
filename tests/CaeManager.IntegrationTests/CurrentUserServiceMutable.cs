using CaeManager.Application.Common;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Usuario de sesión que un test puede cambiar entre pasos, para arneses que se
/// crean una vez y ejecutan como usuarios distintos. Desde la RLS de
/// <c>AspNetUsers</c> (P1-M1), <c>app.usuario_id</c> y <c>app.tenant_origen_id</c>
/// deciden si la propia cuenta es visible fuera de su Tenant: un arnés sin usuario
/// de sesión deja de reproducir producción en cuanto la sesión opera otro Tenant.
/// </summary>
public sealed class CurrentUserServiceMutable : ICurrentUserService
{
    public Guid? UsuarioId { get; set; }
    public Guid? TenantOrigenId { get; set; }
    public string? Rol { get; set; }

    public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(UsuarioId);
    public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(Rol);
    public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult(Rol);
    public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(TenantOrigenId);
    public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
}
