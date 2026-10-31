using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ClaveApiRepository(CaeManagerDbContext dbContext) : IClaveApiRepository
{
    public Task<ClaveApi?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ClavesApi.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    /// <summary>
    /// La única lectura del sistema que resuelve un tenant sin tenerlo, y por
    /// eso la única que no puede ir por EF: sin <c>app.tenant_id</c> fijado, la
    /// política <c>aislamiento_tenant</c> de <c>ClavesApi</c> compara contra
    /// <c>NULL</c> y devuelve cero filas bajo <c>cae_app_runtime</c> — que es
    /// el rol con el que produce el tráfico. <c>IgnoreQueryFilters()</c>, que
    /// es lo que había aquí antes, solo quitaba el filtro de EF; la política
    /// seguía en pie y toda clave recibía 401 (reproducido por HTTP en
    /// <c>ApiPublicaBajoRolRuntimeTests</c> antes de corregir nada).
    ///
    /// <para>
    /// <c>app_tenant_de_clave_api</c> es <c>SECURITY DEFINER</c> y devuelve un
    /// único <c>uuid</c>: ni la fila ni ninguna otra columna sale de RLS. El
    /// valor va parametrizado por EF (<c>SqlQuery</c> interpolado), nunca
    /// concatenado. Ver la migración
    /// 20260921155801_ResolucionDeClaveApiBajoRls para qué se puede aprender
    /// llamándola y por qué es aceptable.
    /// </para>
    /// </summary>
    public async Task<Guid?> ObtenerTenantPorHashAsync(string hashClave, CancellationToken cancellationToken = default) =>
        await dbContext.Database
            .SqlQuery<Guid?>($"SELECT app_tenant_de_clave_api({hashClave}) AS \"Value\"")
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Sin <c>IgnoreQueryFilters()</c> desde que
    /// <see cref="ObtenerTenantPorHashAsync"/> existe: el llamador ya entró en
    /// el <c>AmbitoTenantExplicito</c> del Tenant dueño de la clave, así que
    /// esta lectura pasa por el filtro global Y por la política RLS, como
    /// cualquier otra. Es más estricta que la versión anterior, no menos.
    /// </summary>
    public Task<ClaveApi?> ObtenerPorHashAsync(string hashClave, CancellationToken cancellationToken = default) =>
        dbContext.ClavesApi.FirstOrDefaultAsync(c => c.HashClave == hashClave, cancellationToken);

    public void Agregar(ClaveApi clave) => dbContext.ClavesApi.Add(clave);

    public async Task<bool> ClaveSigueRespaldadaAsync(
        Guid tenantPropietarioId, Guid creadaPorUsuarioId, DateTime emitidaEnUtc,
        Guid? ventanaSoporteId, DateTime? ventanaSoporteAbiertaEnUtc, CancellationToken cancellationToken = default)
    {
        var ahora = DateTime.UtcNow;

        // "Tenants" no tiene filtro global ni RLS y las dos identidades de la aplicación pueden
        // leerla. Sin la fila no hay respaldo.
        var tenant = await dbContext.Tenants
            .Where(t => t.Id == tenantPropietarioId)
            .Select(t => new { t.Estado, t.EstadoComercial, t.ClavesApiValidasDesdeUtc })
            .SingleOrDefaultAsync(cancellationToken);
        if (tenant is null || !Tenant.AdmiteClaveEmitidaEn(tenant.Estado, tenant.EstadoComercial, tenant.ClavesApiValidasDesdeUtc, emitidaEnUtc))
            return false;

        var estado = await AlmacenUsuarios.EstadoCuentaEmisoraAsync(dbContext, creadaPorUsuarioId, cancellationToken);
        if (estado is null || !ApplicationUser.AdmiteClaveEmitidaEn(
                estado.FinBloqueo, estado.ClavesApiValidasDesdeUtc, emitidaEnUtc, new DateTimeOffset(ahora)))
            return false;

        // Corre bajo el ámbito del Tenant propietario de la clave: "DelegacionesTenant" no tiene
        // filtro global de EF, y su RLS permite leer desde ese Tenant (TenantClienteId), así que
        // una clave legítima no puede dar un falso negativo por RLS.
        return ventanaSoporteId is { } ventana && ventanaSoporteAbiertaEnUtc is { } abierta
            && await dbContext.DelegacionesTenant
                .AnyAsync(CriterioSoporteClaveApi.PuedeAutenticar(ventana, abierta, tenantPropietarioId, ahora), cancellationToken);
    }
}
