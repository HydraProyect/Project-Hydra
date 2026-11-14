using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;

namespace CaeManager.Application.Operaciones;

/// <summary>
/// El predicado ÚNICO de procedencia de una Asignación de Operación externa: solo autoriza mientras
/// la <see cref="DelegacionTenant"/> concreta de la que nació (<see cref="AsignacionOperacion.DelegacionTenantOrigenId"/>)
/// siga siendo de Operador CAE externo y del mismo par, esté activa, no haya caducado y no se haya
/// revocado ninguna vez desde que nació la operación (<see cref="AsignacionOperacion.RevocacionesDelegacionOrigen"/>
/// igual a <see cref="DelegacionTenant.Revocaciones"/>). Es la versión en SQL de
/// <see cref="AsignacionOperacion.EstaAmparadaPor"/>.
///
/// <para>
/// Cierra dos vías que antes abrían acceso (hallazgos de Codex en la PR #2223): otra delegación
/// vigente del mismo par ya no sostiene una operación de otro origen, y una operación que sobrevivió
/// a la revocación de su delegación —por la ranura anterior del despliegue azul-verde o por cualquier
/// escritor que no la cerrara— no vuelve a autorizar cuando esa delegación se reactiva: reactivar no
/// repone nada (opción A, decisión de Chris del 2026-11-14). Sin origen (<c>NULL</c>), una operación
/// externa no autoriza: falla cerrada.
/// </para>
///
/// <para>
/// La raíz y las operaciones internas (propietario = operador) no dependen de ninguna delegación y
/// pasan siempre. Lo aplican todos los lectores de alcance que leen operaciones externas: las dos
/// primitivas de <c>TenantsBeneficiariosAutorizados</c> (con cartera y del Tenant entero),
/// <c>AmbitosEfectivosDeCarteraAsync</c>, <c>AlcanceDatosService.ObtenerCarteraAsync</c>,
/// <c>CatalogoIncorporacionCartera</c>, <c>ContextoOperativoConversacionService</c> y
/// <c>DirectorioUsuariosTenant</c>.
/// </para>
/// </summary>
public static class AmparoDeDelegacionDeOrigen
{
    public static IQueryable<AsignacionOperacion> AmparadasPorSuDelegacionDeOrigen(
        this IQueryable<AsignacionOperacion> operaciones, IQueryable<DelegacionTenant> delegaciones, DateTime ahora) =>
        operaciones.Where(operacion =>
            operacion.PropietarioTenantId == operacion.OperadorTenantId
            || delegaciones.Any(delegacion =>
                delegacion.Id == operacion.DelegacionTenantOrigenId
                && delegacion.TenantClienteId == operacion.PropietarioTenantId
                && delegacion.TenantConsultoraId == operacion.OperadorTenantId
                && delegacion.Proposito == PropositoDelegacion.OperadorExterno
                && delegacion.Activa
                && (delegacion.ExpiraEnUtc == null || ahora < delegacion.ExpiraEnUtc)
                && delegacion.Revocaciones == operacion.RevocacionesDelegacionOrigen));
}
