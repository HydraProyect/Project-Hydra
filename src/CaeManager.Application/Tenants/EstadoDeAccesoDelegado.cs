using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants;

/// <summary>
/// Lo que la pantalla necesita saber de un acceso delegado (legacy <c>DelegacionTenant</c>)
/// para decidir si pinta «Revocar acceso» o «Reactivar»: las dos consultas
/// (<c>PuedeRevocarQuery</c> y <c>PuedeReactivarQuery</c>) leen la misma proyección, así que
/// las dos ven la fila igual. La lectura va con el Tenant activo de la sesión, como el resto de
/// la pantalla; no abre ningún ámbito ni concede nada: la autoridad la decide después
/// <see cref="GestorCaeDelOperadorCaeExterno"/>.
/// </summary>
/// <param name="OperadorTenantId">El Tenant del Operador CAE externo (legacy <c>TenantConsultoraId</c>).</param>
/// <param name="TenantPropietarioId">El Tenant propietario de los datos (legacy <c>TenantClienteId</c>).</param>
/// <param name="EsDeOperadorExterno">Si es la Asignación de Operación de un Operador CAE externo y no un acceso de Soporte TALVEG.</param>
/// <param name="Activa">Si sigue vigente.</param>
internal sealed record EstadoDeAccesoDelegado(
    Guid OperadorTenantId, Guid TenantPropietarioId, bool EsDeOperadorExterno, bool Activa)
{
    public static Task<EstadoDeAccesoDelegado?> LeerAsync(
        ITenantsQueryContext tenantsContext, Guid id, CancellationToken cancellationToken) =>
        tenantsContext.DelegacionesTenant
            .Where(d => d.Id == id)
            .Select(d => new EstadoDeAccesoDelegado(
                d.TenantConsultoraId, d.TenantClienteId,
                d.Proposito == PropositoDelegacion.OperadorExterno, d.Activa))
            .SingleOrDefaultAsync(cancellationToken);
}
