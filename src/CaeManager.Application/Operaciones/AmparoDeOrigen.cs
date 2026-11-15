using System.Linq.Expressions;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;

namespace CaeManager.Application.Operaciones;

/// <summary>
/// El predicado ÚNICO de procedencia de una Asignación de Operación externa: solo autoriza mientras
/// la <see cref="DelegacionTenant"/> concreta de la que nació (<see cref="AsignacionOperacion.OrigenId"/>)
/// siga siendo de Operador CAE externo y del mismo par, esté activa, no haya caducado y no se haya
/// revocado ninguna vez desde que nació la operación (<see cref="AsignacionOperacion.RevocacionesDelOrigen"/>
/// igual a <see cref="DelegacionTenant.Revocaciones"/>). Otra delegación vigente del par no sostiene una
/// operación de otro origen, y una operación que sobrevivió a la revocación de su delegación no vuelve a
/// autorizar cuando esa delegación se reactiva: reactivar no repone nada (opción A, decisión de Chris del
/// 2026-11-14). La raíz y las operaciones internas (propietario = operador) no dependen de ninguna
/// delegación y pasan siempre.
///
/// <para>
/// <b>Fase de expansión</b> (decisión de Chris del 2026-11-15 sobre la PR #2237, opción c). Los lectores de
/// alcance aplican hoy <see cref="SinOrigenOAmparadasPorSuOrigen"/>: el amparo se exige solo a la operación
/// externa que tiene origen, y una sin origen (las anteriores a la migración <c>AnadeOrigenDeOperacionExterna</c>,
/// y las que el trigger <c>asignacion_operacion_sella_origen</c> no pudo deducir) conserva el comportamiento
/// anterior: la decide el resto del predicado de cada lector, que no cambia. La PR #2237 retira esa excepción:
/// cierra las operaciones externas sin origen y pasa los lectores a <see cref="AmparadasPorSuOrigen"/>, que falla
/// cerrado sin origen. Los dos métodos comparten la misma expresión, para que no haya dos definiciones del amparo.
/// </para>
///
/// <para>
/// Lo aplican los lectores de alcance que leen operaciones externas: las dos primitivas de
/// <c>TenantsBeneficiariosAutorizados</c> (con cartera y del Tenant entero), <c>AmbitosEfectivosDeCarteraAsync</c>,
/// <c>AlcanceDatosService.ObtenerCarteraAsync</c>, <c>ContextoOperativoConversacionService</c>,
/// <c>DirectorioUsuariosTenant.ObtenerCarterasVigentesAsync</c> y <c>VistaDemoActual</c>; y el escritor que parte de
/// una operación para emitir una cartera y su fila heredada, <c>CatalogoIncorporacionCartera</c>. No lo aplica
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

    /// <summary>
    /// <see cref="AmparadasPorSuOrigen"/> más las operaciones sin origen, que siguen como antes de la fase de
    /// expansión. Es el filtro de los lectores de alcance hasta la PR #2237.
    /// </summary>
    public static IQueryable<AsignacionOperacion> SinOrigenOAmparadasPorSuOrigen(
        this IQueryable<AsignacionOperacion> operaciones, IQueryable<DelegacionTenant> delegaciones, DateTime ahora)
    {
        var amparo = Amparo(delegaciones, ahora);
        var operacion = amparo.Parameters[0];
        var sinOrigen = Expression.Equal(
            Expression.Property(operacion, nameof(AsignacionOperacion.OrigenId)),
            Expression.Constant(null, typeof(Guid?)));

        return operaciones.Where(
            Expression.Lambda<Func<AsignacionOperacion, bool>>(Expression.OrElse(sinOrigen, amparo.Body), operacion));
    }

    /// <summary>
    /// <see cref="SinOrigenOAmparadasPorSuOrigen(IQueryable{AsignacionOperacion}, IQueryable{DelegacionTenant}, DateTime)"/>
    /// con las delegaciones del contexto de consulta de Tenants, para los escritores que leen operaciones y
    /// delegaciones del mismo contexto (<c>CatalogoIncorporacionCartera</c>).
    /// </summary>
    public static IQueryable<AsignacionOperacion> SinOrigenOAmparadasPorSuOrigen(
        this IQueryable<AsignacionOperacion> operaciones, ITenantsQueryContext tenants, DateTime ahora) =>
        operaciones.SinOrigenOAmparadasPorSuOrigen(tenants.DelegacionesTenant, ahora);

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
