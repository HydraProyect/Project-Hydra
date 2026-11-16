using System.Linq.Expressions;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;

namespace CaeManager.Application.Operaciones;

/// <summary>
/// El predicado ÚNICO de procedencia de una Asignación de Operación externa: solo autoriza mientras
/// la <see cref="DelegacionTenant"/> concreta de la que nació (<see cref="AsignacionOperacion.OrigenId"/>)
/// siga siendo de Operador CAE externo y del mismo par, esté activa, no haya caducado y no se haya
/// revocado ninguna vez desde que nació la operación (<see cref="AsignacionOperacion.RevocacionesDelOrigen"/>
/// igual a <see cref="DelegacionTenant.Revocaciones"/>). Es la versión en SQL de
/// <see cref="AsignacionOperacion.EstaAmparadaPor"/>.
///
/// <para>
/// Cierra dos vías que antes abrían acceso (hallazgos de Codex en la PR #2223): otra delegación
/// vigente del mismo par ya no sostiene una operación de otro origen, y una operación que sobrevivió
/// a la revocación de su delegación —por la ranura anterior del despliegue azul-verde o por cualquier
/// escritor que no la cerrara— no vuelve a autorizar cuando esa delegación se reactiva: reactivar no
/// repone nada (opción A, decisión de Chris del 2026-11-14). Sin origen (<c>NULL</c>), una operación
/// externa no autoriza: falla cerrada. Es la fase de contracción de la PR #2237 (decisión de Chris del
/// 2026-11-15, opción c): la fase de expansión de la PR #2253 dejaba pasar todavía las operaciones sin origen.
/// </para>
///
/// <para>
/// La raíz y las operaciones internas (propietario = operador) no dependen de ninguna delegación y
/// pasan siempre. Lo aplican los lectores de alcance que leen operaciones externas: las dos primitivas de
/// <c>TenantsBeneficiariosAutorizados</c> (con cartera y del Tenant entero), <c>AmbitosEfectivosDeCarteraAsync</c>,
/// <c>AlcanceDatosService.ObtenerCarteraAsync</c>, <c>ContextoOperativoConversacionService</c>,
/// <c>DirectorioUsuariosTenant.ObtenerCarterasVigentesAsync</c> y <c>VistaDemoActual</c>; la guarda que deja al
/// Operador CAE fijar la tarifa de un Tenant beneficiario, <c>AutorizacionTarifasOperadorCae.GestionaAsync</c>; y el
/// escritor que parte de una operación para emitir una cartera y su fila heredada, <c>CatalogoIncorporacionCartera</c>,
/// que antes de decidir bloquea con <c>FOR SHARE</c> la DelegacionTenant del par dentro de la transacción del guardado,
/// para que una revocación no se confirme entre la comprobación y la emisión. <c>EncargoAdministracionRepository</c>
/// aplica el mismo predicado en PostgreSQL (<c>app_operacion_amparada_por_su_origen</c>, vía
/// <c>FuncionAmparoDeOrigen</c>), porque una de sus vías lee como <c>cae_app_aprovisionamiento</c>, sin <c>SELECT</c>
/// sobre <c>DelegacionesTenant</c>; un test de integración compara las dos versiones. No lo aplica
/// <c>DirectorioUsuariosTenant.TieneVinculoOperativoAsync</c>: es una guarda de integridad (no borrar una cuenta con
/// una Asignación de Cartera vigente) y no de autorización.
/// </para>
/// </summary>
public static class AmparoDeOrigen
{
    /// <summary>
    /// Las Asignaciones de Operación que su origen ampara en <paramref name="ahora"/>: la raíz y las internas
    /// siempre; una externa solo con su DelegacionTenant de origen activa, sin caducar y sin revocaciones desde
    /// que nació. Una externa sin origen no pasa.
    /// </summary>
    public static IQueryable<AsignacionOperacion> AmparadasPorSuOrigen(
        this IQueryable<AsignacionOperacion> operaciones, IQueryable<DelegacionTenant> delegaciones, DateTime ahora) =>
        operaciones.Where(Amparo(delegaciones, ahora));

    private static Expression<Func<AsignacionOperacion, bool>> Amparo(
        IQueryable<DelegacionTenant> delegaciones, DateTime ahora) =>
        operacion =>
            operacion.PropietarioTenantId == operacion.OperadorTenantId
            || delegaciones.Any(delegacion =>
                delegacion.Id == operacion.OrigenId
                && delegacion.TenantClienteId == operacion.PropietarioTenantId
                && delegacion.TenantConsultoraId == operacion.OperadorTenantId
                && delegacion.Proposito == PropositoDelegacion.OperadorExterno
                && delegacion.Activa
                && (delegacion.ExpiraEnUtc == null || ahora < delegacion.ExpiraEnUtc)
                && delegacion.Revocaciones == operacion.RevocacionesDelOrigen);
}
