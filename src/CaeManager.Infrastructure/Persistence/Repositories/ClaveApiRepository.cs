using CaeManager.Domain.ApiKeys;
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

    public async Task<bool> CuentaEmisoraRespaldaClaveAsync(Guid creadaPorUsuarioId, DateTime emitidaEnUtc, CancellationToken cancellationToken = default)
    {
        var estado = await AlmacenUsuarios.EstadoCuentaEmisoraAsync(dbContext, creadaPorUsuarioId, cancellationToken);
        return estado is not null && ApplicationUser.AdmiteClaveEmitidaEn(
            estado.FinBloqueo, estado.UltimaDesactivacionEnUtc, emitidaEnUtc, DateTimeOffset.UtcNow);
    }

    public async Task<bool> ExisteSoporteDelOrigenEmisorAsync(Guid tenantPropietarioId, Guid creadaPorUsuarioId, CancellationToken cancellationToken = default)
    {
        // Solo existencia de la delegación de Soporte (CriterioSoporteClaveApi.PuedeAutenticar):
        // ni Activa ni ExpiraEnUtc, porque la ventana de soporte dura horas y la clave vive hasta
        // su caducidad. Si la cuenta emisora se ha eliminado, app_tenant_de_cuenta devuelve null
        // y esto es false.
        //
        // Corre bajo el ámbito del Tenant propietario de la clave. Ambas lecturas funcionan
        // desde ese ámbito, así que una clave legítima no puede dar un falso negativo por RLS:
        // - la cuenta emisora es de otra organización y "AspNetUsers" bajo RLS no la vería; su
        //   Tenant se resuelve con app_tenant_de_cuenta (SECURITY DEFINER, EXECUTE para
        //   cae_app_runtime, devuelve solo el TenantId de esa cuenta), sin ampliar la política;
        // - "DelegacionesTenant" no tiene filtro global de EF, pero su RLS permite leer
        //   desde el Tenant propietario de la clave (TenantClienteId), con SELECT para cae_app_runtime.
        var origen = await AlmacenUsuarios.TenantDeCuentaAsync(dbContext, creadaPorUsuarioId, cancellationToken);
        return origen is { } tenantOrigenId && await dbContext.DelegacionesTenant
            .AnyAsync(CriterioSoporteClaveApi.PuedeAutenticar(tenantOrigenId, tenantPropietarioId), cancellationToken);
    }
}
