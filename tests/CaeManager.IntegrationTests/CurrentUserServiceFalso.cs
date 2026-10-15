using CaeManager.Application.Common;

namespace CaeManager.IntegrationTests;

/// <param name="rol">Rol efectivo en el Tenant que se opera.</param>
/// <param name="rolOrigen">
/// Rol de Identity en la organización de origen. Por defecto el mismo que
/// <paramref name="rol"/>: el caso de un usuario operando su propio Tenant.
/// Distinto solo para modelar un Workspace operativo derivado (decisión P7).
/// </param>
public class CurrentUserServiceFalso(
    Guid? usuarioId = null, string? rol = null, Guid? tenantOrigenId = null, bool tieneDobleFactorActivo = true,
    string? rolOrigen = null, Guid? encargoPleno = null)
    : ICurrentUserService, IEncargoDeAdministracionActual
{
    // Este doble no calcula el Encargo de administración: por defecto nunca eleva. Con
    // encargoPleno declara un encargo pleno ya resuelto (el rol lo fija quien construye el doble),
    // para probar bajo RLS lo que el encargo pleno abre. Tiene que decirlo él porque AddApplication()
    // falla si el ICurrentUserService registrado no da la señal.
    public Task<Guid?> EncargoQueElevaAsync() => Task.FromResult(encargoPleno);

    public Guid? EncargoDeLaUltimaResolucion(Guid asignacionOperacionId) => encargoPleno;

    public Task<Guid?> EncargoPlenoAsync() => Task.FromResult(encargoPleno);

    public bool EncargoPlenoDeLaUltimaResolucion(Guid asignacionOperacionId) => encargoPleno is not null;

    public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(usuarioId);

    public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(rol);

    public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult(rolOrigen ?? rol);

    public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(tenantOrigenId);

    public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(tieneDobleFactorActivo);
}
