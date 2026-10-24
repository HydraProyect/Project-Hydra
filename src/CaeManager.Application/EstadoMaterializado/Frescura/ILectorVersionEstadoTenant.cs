namespace CaeManager.Application.EstadoMaterializado.Frescura;

/// <summary>La versión del estado materializado de un Tenant, tal como la ve la conexión que la leyó.</summary>
public sealed record VersionEstadoLeida(Guid TenantId, long Version);

/// <summary>
/// Lee la versión del estado materializado del Tenant de la conexión (diseño § 5.1): una sentencia sobre
/// <c>VersionesEstadoTenant</c>, con el contexto RLS del ámbito, que solo deja ver la fila del Tenant de
/// <c>app.tenant_id</c>. Scoped.
/// </summary>
public interface ILectorVersionEstadoTenant
{
    /// <summary>
    /// La versión del Tenant de la conexión, o <c>null</c> si no hay exactamente una fila visible: sin contexto de Tenant,
    /// con un Tenant aún sin relleno (sin fila, diseño § 7.3) o con una identidad que ve varios Tenants. Con <c>null</c> no
    /// se cachea nada.
    /// </summary>
    Task<VersionEstadoLeida?> LeerAsync(CancellationToken cancellationToken);
}
