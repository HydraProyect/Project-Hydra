using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Configuracion;

/// <summary>
/// Quién puede cambiar la seguridad de acceso de un Tenant: su propio Administrador, leído en la
/// base (<see cref="IAdministradorDelTenantPropietario"/>), como con el contacto de seguridad. Con
/// otro Tenant seleccionado por la vía de Operación, un Gestor CAE o el Administrador del Operador
/// CAE externo no pasan, y Soporte TALVEG en Sesión Privilegiada tampoco. El Tenant es siempre el
/// actual, nunca un parámetro.
///
/// <para>
/// El Tenant de plataforma queda fuera: sus cuentas son las de los Actores de Plataforma TALVEG, que
/// conservan 30 minutos y nunca podrían rebajarse desde esta pantalla.
/// </para>
/// </summary>
public static class AutoridadSeguridadAcceso
{
    public static readonly Error SoloAdministradorDelTenant = Error.Crear(
        "SeguridadAcceso.SoloAdministradorDelTenant",
        "Solo el Administrador de esta organización puede cambiar la seguridad de acceso.");

    public static async Task<bool> PuedeEditarAsync(
        ITenantActual tenantActual,
        ICurrentUserService currentUserService,
        IAdministradorDelTenantPropietario administradorDelTenant,
        ITenantsQueryContext tenants,
        CancellationToken cancellationToken) =>
        tenantActual.TenantId is { } tenantId
        && await currentUserService.ObtenerUsuarioActualIdAsync() is { } usuarioId
        && await administradorDelTenant.EsAdministradorEnBaseAsync(usuarioId, tenantId, cancellationToken)
        && await tenants.Tenants.AnyAsync(t => t.Id == tenantId && !t.EsPlataforma, cancellationToken);
}
