using CaeManager.Domain.Tenants;

namespace CaeManager.Application.Tenants;

/// <summary>
/// Acceso de solo lectura al catálogo de Encargos de administración.
///
/// <b>Está fuera del filtro global de Tenant, y eso no lo hace legible sin
/// restricción.</b> Una fila dice qué Tenant propietario ha encargado su
/// administración a qué Operador CAE externo y con qué cláusula: es metadata
/// contractual. Toda consulta se acota a la posición del llamante, la misma
/// regla que <see cref="Operaciones.IOperacionesQueryContext"/>:
/// <list type="bullet">
/// <item>desde el Tenant propietario → <c>PropietarioTenantId</c> = el Tenant activo;</item>
/// <item>desde el Operador CAE externo → <c>OperadorTenantId</c> = su <b>Tenant
/// de origen</b>, nunca el Tenant activo.</item>
/// </list>
/// La política RLS <c>posicion_en_el_encargo</c> exige lo mismo por debajo.
/// Interfaz propia y no un miembro más de <see cref="ITenantsQueryContext"/>
/// para que la lista de sitios que la tocan sea corta y la vigile
/// <c>AccesoRestringidoACatalogosDeAsignacionTests</c>.
/// </summary>
public interface IEncargosAdministracionQueryContext
{
    IQueryable<EncargoAdministracion> EncargosAdministracion { get; }
}
