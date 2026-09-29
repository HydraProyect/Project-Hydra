namespace CaeManager.Application.Tenants;

/// <summary>
/// ¿Es este usuario, ahora mismo, Administrador de ese Tenant propietario? Pertenencia y rol leídos de
/// la base (Identity), nunca de la claim ni del rol efectivo del Context Workspace: dentro de un Tenant
/// alcanzado por Operación el rol efectivo es el de la cartera, y en el Tenant de origen el rol efectivo
/// sale de la claim sin consultar la base (revisión Codex C6 del contrato del selector de Tenant).
/// Un usuario de plataforma nunca pertenece al Tenant objetivo, así que queda fuera sin comprobarlo.
/// </summary>
public interface IAdministradorDelTenantPropietario
{
    Task<bool> EsAdministradorEnBaseAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default);
}
