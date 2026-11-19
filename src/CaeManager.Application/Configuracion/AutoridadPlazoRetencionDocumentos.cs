using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Configuracion;

/// <summary>
/// Quién fija el plazo de conservación de los Documentos, el del Tenant propietario y el de cada tipo de documento
/// (decisión del 2026-11-18): el Administrador del Tenant actual, leído en la base, como la seguridad de acceso
/// (<see cref="AutoridadSeguridadAcceso"/>). Un Gestor CAE o el Administrador de un Operador CAE externo que entra por
/// Asignación de Operación no es Administrador del Tenant propietario en la base y no pasa. Un Actor de Plataforma
/// TALVEG en Sesión Privilegiada, revalidada en la base, tampoco, ni siquiera viendo como un Usuario simulado que sí lo
/// fuera: el plazo decide qué datos se proponen destruir y lo responde el Tenant propietario. El Tenant es siempre el
/// actual, nunca un parámetro. Las dos peticiones están además fuera del Encargo de administración
/// (<see cref="Tenants.Encargo.ActosExcluidosDelEncargo"/>). La auditoría del cambio, con el Actor real, la deja el
/// interceptor de auditoría al guardar la entidad.
/// </summary>
public static class AutoridadPlazoRetencionDocumentos
{
    public static readonly Error SoloAdministradorDelTenant = Error.Crear(
        "PlazoRetencion.SoloAdministradorDelTenant",
        "Solo el Administrador de esta organización puede cambiar el plazo de conservación de los documentos.");

    public static async Task<bool> PuedeFijarAsync(
        ISesionPrivilegiadaActual sesionPrivilegiada,
        ITenantActual tenantActual,
        ICurrentUserService currentUserService,
        IAdministradorDelTenantPropietario administradorDelTenant,
        ITenantsQueryContext tenants,
        CancellationToken cancellationToken) =>
        await sesionPrivilegiada.RevalidarAsync(cancellationToken) is null
        && await AutoridadSeguridadAcceso.PuedeEditarAsync(
            tenantActual, currentUserService, administradorDelTenant, tenants, cancellationToken);
}
