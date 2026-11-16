using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Persistence;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Siembra de una Asignación de Operación externa con la DelegacionTenant de Operador CAE externo de la que nace, como
/// la abre el alta real (<c>AsignacionesOperativasWriter</c>). Una operación externa sin origen sellado no autoriza en
/// ningún lector de alcance (<c>AmparoDeOrigen</c>, PR #2237), y el trigger solo deduce el origen de una Outbound
/// cuando el par tiene una única delegación activa: una Inbound, o un par con dos delegaciones, quedaría sin origen.
/// Para los fixtures cuya prueba no trata de la procedencia; los que sí, la siembran a mano.
/// </summary>
internal static class OperacionExternaAmparada
{
    /// <summary>
    /// Añade <paramref name="operacion"/> al contexto amparada por <paramref name="origen"/> o, sin él, por una
    /// DelegacionTenant nueva de su par, que también se añade. Devuelve la delegación de origen. Sin guardar.
    /// </summary>
    public static DelegacionTenant Agregar(
        CaeManagerDbContext contexto, AsignacionOperacion operacion, DelegacionTenant? origen = null)
    {
        if (origen is null)
        {
            origen = new DelegacionTenant(operacion.OperadorTenantId, operacion.PropietarioTenantId);
            contexto.DelegacionesTenant.Add(origen);
        }

        operacion.FijarOrigen(origen);
        contexto.AsignacionesOperacion.Add(operacion);
        return origen;
    }
}
