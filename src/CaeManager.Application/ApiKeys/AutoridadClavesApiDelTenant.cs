using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;

namespace CaeManager.Application.ApiKeys;

/// <summary>
/// Quién ve y revoca desde el Tenant propietario sus claves de API: solo su propio Administrador,
/// leído en la base (<see cref="IAdministradorDelTenantPropietario"/>; decisión del 2026-10-31;
/// OWASP ASVS 4.0.3 V4.1.1 y V4.1.3). Con otro Tenant seleccionado por la vía de Operación, un
/// Gestor CAE o el Administrador del Operador CAE externo no pasan, y Soporte TALVEG en Sesión
/// Privilegiada tampoco: tenant isolation ≠ operational delegation ≠ platform privilege. El Tenant
/// es siempre el actual, nunca un parámetro. El Actor de Plataforma TALVEG revoca por su propia
/// vía, <see cref="Commands.RevocarClaveApi.RevocarClaveApiCommand"/>.
/// </summary>
public static class AutoridadClavesApiDelTenant
{
    public static readonly Error SoloAdministradorDelTenant = Error.Crear(
        "ClaveApi.SoloAdministradorDelTenant",
        "Solo el Administrador de esta organización puede gestionar sus claves.");

    public static async Task<bool> PuedeGestionarAsync(
        ITenantActual tenantActual,
        ICurrentUserService currentUserService,
        IAdministradorDelTenantPropietario administradorDelTenant,
        CancellationToken cancellationToken) =>
        tenantActual.TenantId is { } tenantId
        && await currentUserService.ObtenerUsuarioActualIdAsync() is { } usuarioId
        && await administradorDelTenant.EsAdministradorEnBaseAsync(usuarioId, tenantId, cancellationToken);
}
