using CaeManager.Domain.Operaciones;

namespace CaeManager.Application.Operaciones;

/// <summary>
/// Acceso de solo lectura a los catálogos globales de asignación de
/// responsabilidad operativa.
///
/// <b>Estas dos tablas están fuera del filtro global de tenant, y eso no las
/// hace legibles sin restricción.</b> Una fila revela quién opera para quién y
/// sobre qué ámbito: es metadata empresarial. Toda consulta debe acotarse a la
/// posición del llamante —
/// <list type="bullet">
/// <item>usuario del propietario → <c>PropietarioTenantId</c> = su tenant;</item>
/// <item>usuario del operador → <c>OperadorTenantId</c> = su <b>tenant de
/// origen</b>, nunca el tenant actual (dentro de un workspace delegado el
/// actual es el del propietario, y la consulta "mis workspaces" no devolvería
/// nada);</item>
/// <item>plataforma → según su privilegio.</item>
/// </list>
/// No existe ni debe existir un "listar todas". Un test de arquitectura vigila
/// que nadie toque estos DbSets fuera de los servicios autorizados.
/// </summary>
public interface IOperacionesQueryContext
{
    IQueryable<AsignacionOperacion> AsignacionesOperacion { get; }
    IQueryable<AsignacionCartera> AsignacionesCartera { get; }
}
