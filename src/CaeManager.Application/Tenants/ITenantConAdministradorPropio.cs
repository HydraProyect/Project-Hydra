namespace CaeManager.Application.Tenants;

/// <summary>
/// ¿Tiene el Tenant propietario un Administrador propio que pueda actuar? Una
/// cuenta <i>miembro</i> del Tenant con rol Administrador y no desactivada, esté
/// activada o pendiente de activación: una invitación de Administrador pendiente
/// cuenta, para que el Operador CAE con encargo no pueda redirigirla ni eliminarla
/// y quedarse con ese puesto. Decide si un Encargo de administración es
/// <b>pleno</b> (decisión de Chris, 2026-10-15): solo lo es en un Tenant
/// propietario sin Administrador propio.
///
/// <para>
/// Falla cerrado: ante cualquier duda contesta que sí, porque «no tiene» es lo
/// que levanta las exclusiones del encargo.
/// </para>
/// </summary>
public interface ITenantConAdministradorPropio
{
    Task<bool> TieneAdministradorPropioAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
