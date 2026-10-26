using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants;

/// <summary>
/// Lo que la pantalla necesita saber de un acceso delegado (legacy <c>DelegacionTenant</c>)
/// para decidir si pinta «Revocar acceso» o «Reactivar»: las dos consultas
/// (<c>PuedeRevocarQuery</c> y <c>PuedeReactivarQuery</c>) leen la misma proyección, así que
/// las dos ven la fila igual. La lectura va con el Tenant activo de la sesión, como el resto de
/// la pantalla; no abre ningún ámbito ni concede nada: la autoridad la decide después
/// <see cref="EquipoDelOperadorCaeExterno"/>.
/// </summary>
/// <param name="Id">El Id del acceso delegado.</param>
/// <param name="OperadorTenantId">El Tenant del Operador CAE externo (legacy <c>TenantConsultoraId</c>).</param>
/// <param name="TenantPropietarioId">El Tenant propietario de los datos (legacy <c>TenantClienteId</c>).</param>
/// <param name="EsDeOperadorExterno">Si es la Asignación de Operación de un Operador CAE externo y no un acceso de Soporte TALVEG.</param>
/// <param name="Activa">Si sigue vigente.</param>
/// <param name="EsTenantPlataforma">Si el Tenant propietario es el Tenant de plataforma, leído en el
/// Tenant activo y bajo RLS como en <c>PuedeReactivarAsync</c>; solo lo rellena la lectura por lotes
/// (la individual lo deja en <c>false</c> y lo comprueba su propio handler).</param>
internal sealed record EstadoDeAccesoDelegado(
    Guid Id, Guid OperadorTenantId, Guid TenantPropietarioId, bool EsDeOperadorExterno, bool Activa, bool EsTenantPlataforma)
{
    internal static bool PermiteReactivar(bool esDeOperadorExterno, bool esTenantPlataforma) =>
        esDeOperadorExterno && !esTenantPlataforma;

    internal PermisosAccesoDelegadoDto Permisos(bool autorizado) => new(
        autorizado && EsDeOperadorExterno && Activa,
        autorizado && !Activa && PermiteReactivar(EsDeOperadorExterno, EsTenantPlataforma));

    public static async Task<EstadoDeAccesoDelegado?> LeerAsync(
        ITenantsQueryContext tenantsContext, Guid id, CancellationToken cancellationToken) =>
        (await LeerEnLoteAsync(tenantsContext, [id], cancellationToken, incluirPlataforma: false)).SingleOrDefault();

    internal static Task<List<EstadoDeAccesoDelegado>> LeerEnLoteAsync(
        ITenantsQueryContext tenantsContext, Guid[] ids, CancellationToken cancellationToken,
        bool incluirPlataforma = true) =>
        tenantsContext.DelegacionesTenant
            .Where(d => ids.Contains(d.Id))
            .Select(d => new
            {
                d.Id,
                OperadorTenantId = d.TenantConsultoraId,
                TenantPropietarioId = d.TenantClienteId,
                EsDeOperadorExterno = d.Proposito == PropositoDelegacion.OperadorExterno,
                d.Activa,
            })
            .Select(a => new EstadoDeAccesoDelegado(
                a.Id, a.OperadorTenantId, a.TenantPropietarioId, a.EsDeOperadorExterno, a.Activa,
                incluirPlataforma && tenantsContext.Tenants.Any(t => t.Id == a.TenantPropietarioId && t.EsPlataforma)))
            .ToListAsync(cancellationToken);
}
