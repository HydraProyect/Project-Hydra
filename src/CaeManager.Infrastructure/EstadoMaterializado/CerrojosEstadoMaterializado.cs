using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.EstadoMaterializado;

/// <summary>
/// Cerrojos de asesoramiento de transacción del estado materializado (diseño § 4.4 y § 13.7), compartidos por todos los
/// caminos que escriben estado: el recálculo en línea, la cola y el relleno. Orden único en todos ellos: primero el del
/// Tenant (compartido en línea y en la cola por raíces; exclusivo en el relleno y en la cola de Tenant entero) y después
/// los exclusivos de la clausura, en una sola sentencia y en orden de su hash.
/// </summary>
public static class CerrojosEstadoMaterializado
{
    /// <summary>Clave del cerrojo de asesoramiento del Tenant en el estado materializado.</summary>
    public static string ClaveDeTenant(Guid tenantId) => $"estado_materializado:{tenantId}";

    /// <summary>Clave del cerrojo de una entidad raíz o de la clausura (C Centro, T Trabajador, E Empresa o Subcontrata).</summary>
    public static string ClaveDeEntidad(Guid tenantId, char ambito, Guid id) => $"estado_materializado:{tenantId}:{ambito}:{id}";

    /// <summary>
    /// Cerrojos de transacción, en una sentencia y en orden de su hash: todos los caminos que los toman usan el mismo orden,
    /// así que dos recálculos no se interbloquean entre sí. Una colisión de hash solo serializa de más.
    /// </summary>
    public static Task BloquearAsync(DbContext dbContext, IReadOnlyCollection<string> claves, bool compartido)
    {
        if (claves.Count == 0)
            return Task.CompletedTask;
        var array = claves.Distinct().ToArray();
        return compartido
            ? dbContext.Database.ExecuteSqlAsync($"""
                SELECT pg_advisory_xact_lock_shared(h) FROM (SELECT DISTINCT hashtextextended(k, 0) AS h FROM unnest({array}) AS k ORDER BY 1 OFFSET 0) AS s
                """, CancellationToken.None)
            : dbContext.Database.ExecuteSqlAsync($"""
                SELECT pg_advisory_xact_lock(h) FROM (SELECT DISTINCT hashtextextended(k, 0) AS h FROM unnest({array}) AS k ORDER BY 1 OFFSET 0) AS s
                """, CancellationToken.None);
    }

    /// <summary>
    /// El cerrojo compartido del Tenant sin esperar: <c>false</c> si alguien tiene (o espera) el exclusivo, es decir, si el
    /// relleno o la cola del Tenant entero están trabajando en ese Tenant.
    /// </summary>
    public static async Task<bool> IntentarTenantCompartidoAsync(DbContext dbContext, Guid tenantId)
    {
        var clave = ClaveDeTenant(tenantId);
        return (await dbContext.Database.SqlQuery<bool>($"""
            SELECT pg_try_advisory_xact_lock_shared(hashtextextended({clave}, 0)) AS "Value"
            """).ToListAsync(CancellationToken.None)).Single();
    }

    /// <summary>
    /// Fija <c>lock_timeout</c> en la transacción en curso (<c>set_config(…, true)</c>) y devuelve el que había. Sin tope,
    /// una espera de cerrojo duraría tanto como el tiempo de espera del comando.
    /// </summary>
    public static async Task<string> FijarTopeDeEsperaAsync(DbContext dbContext, string tope)
    {
        var anterior = (await dbContext.Database.SqlQuery<string>($"""
            SELECT current_setting('lock_timeout') AS "Value"
            """).ToListAsync(CancellationToken.None)).Single();
        await dbContext.Database.ExecuteSqlAsync($"""
            SELECT set_config('lock_timeout', {tope}, true)
            """, CancellationToken.None);
        return anterior;
    }

    /// <summary>El Tenant del contexto RLS de la conexión (<c>app.tenant_id</c>), o null sin contexto.</summary>
    public static async Task<Guid?> TenantDeLaConexionAsync(DbContext dbContext) =>
        (await dbContext.Database.SqlQuery<Guid?>($"""
            SELECT NULLIF(current_setting('app.tenant_id', true), '')::uuid AS "Value"
            """).ToListAsync(CancellationToken.None)).SingleOrDefault();

    /// <summary>Si el Tenant ya tiene fila en <c>VersionesEstadoTenant</c>, es decir, si ya se rellenó entero.</summary>
    public static async Task<bool> TieneVersionAsync(DbContext dbContext, Guid tenantId) =>
        (await dbContext.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM "VersionesEstadoTenant" WHERE "TenantId" = {tenantId}) AS "Value"
            """).ToListAsync(CancellationToken.None)).Single();
}
