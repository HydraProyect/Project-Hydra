using CaeManager.Application.Common;
using CaeManager.Application.Usuarios;

namespace CaeManager.Application.Integraciones.Commands;

/// <summary>
/// Quién gestiona la credencial de una conexión con una plataforma CAE de terceros: un Administrador
/// cuyo Tenant de origen es el Tenant propietario. El rol efectivo no basta: en un Workspace operativo
/// derivado es el perfil de origen de quien opera por Asignación de Operación, y un Administrador del
/// Operador CAE externo podría sustituir la credencial del Tenant propietario por la de una cuenta suya
/// (OWASP ASVS 4.0.3 V4.1.3, V4.2.1).
/// </summary>
internal static class AutoridadSobreConexionPlataformaCae
{
    public static async Task<bool> EsAdministradorDelTenantPropietarioAsync(ICurrentUserService usuarioActual, ITenantActual tenantActual) =>
        await usuarioActual.ObtenerRolEfectivoAsync() == "Administrador"
        && !RolesReservadosAlTenantDeOrigen.EsContextoCruzado(await usuarioActual.ObtenerTenantOrigenIdAsync(), tenantActual.TenantId);
}
