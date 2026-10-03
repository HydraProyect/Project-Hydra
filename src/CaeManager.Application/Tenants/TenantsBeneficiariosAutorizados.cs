using CaeManager.Application.Operaciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants;

/// <summary>
/// Un par (Asignación de Cartera, Asignación de Operación) que autoriza a un
/// Gestor CAE a abrir el Tenant beneficiario <see cref="AsignacionOperacion.PropietarioTenantId"/>.
/// Se proyecta con inicializador de miembros (no constructor) para que EF pueda
/// seguir componiendo <c>where</c>/<c>orderby</c> sobre sus miembros.
/// </summary>
public sealed class CarteraBajoOperacionVigente
{
    public required AsignacionCartera Cartera { get; init; }
    public required AsignacionOperacion Operacion { get; init; }
}

/// <summary>
/// Un par (Asignación de Operador Delegado, Delegación) de la vía heredada.
/// </summary>
public sealed class AsignacionHeredadaVigente
{
    public required AsignacionOperadorDelegado Asignacion { get; init; }
    public required DelegacionTenant Concesion { get; init; }
}

/// <summary>
/// El predicado ÚNICO que decide qué Tenants beneficiarios puede activar un
/// usuario como contexto (contrato del selector de Tenant beneficiario, § 4.2.2
/// e invariante I2). Lo usan la lista (<c>ObtenerClientesAutorizadosQuery</c>),
/// el POST <c>/cuenta/cliente-activo</c>, la revalidación del middleware y del
/// circuito, y la resolución del rol efectivo en <c>CurrentUserService</c>.
/// Antes cada uno llevaba su copia y divergían: la revalidación no volvía a
/// exigir operación no raíz ni el Operador CAE de origen (Codex C5), y la lista
/// no incluía los Tenants alcanzados por Operación que el POST sí aceptaba.
///
/// <para>
/// Tres vías, y solo tres:
/// <list type="number">
/// <item><b>Tenant de origen</b>: siempre autorizado sobre sí mismo (lo decide
/// cada llamante comparando con el Tenant de origen; no necesita consulta).</item>
/// <item><b>Operación</b> (<see cref="CarterasPorOperacion"/>): Asignación de
/// Cartera vigente del usuario bajo una Asignación de Operación vigente, no raíz,
/// cuyo Operador CAE es el Tenant de origen del usuario y cuyo Tenant propietario
/// es otro.</item>
/// <item><b>Delegación heredada</b> (<see cref="AsignacionesHeredadasVigentes"/>): la del
/// acceso de soporte, hasta que se retire.</item>
/// </list>
/// </para>
///
/// <para>
/// Cada método recibe el instante <c>ahora</c> del llamante: una sola lectura
/// del reloj por decisión, para que dos condiciones de la misma comprobación no
/// se evalúen en instantes distintos.
/// </para>
///
/// <para>
/// Acceso a los catálogos de asignación acotado a la posición del llamante
/// (<see cref="IOperacionesQueryContext"/>): usuario del Operador CAE →
/// <c>OperadorTenantId</c> = su Tenant de origen, nunca el Tenant actual.
/// </para>
/// </summary>
public static class TenantsBeneficiariosAutorizados
{
    /// <summary>
    /// Vía de Operación: carteras vigentes del usuario bajo operaciones vigentes
    /// externas de su Operador CAE de origen.
    /// </summary>
    public static IQueryable<CarteraBajoOperacionVigente> CarterasPorOperacion(
        IOperacionesQueryContext operaciones, Guid usuarioId, Guid tenantOrigenId, DateTime ahora) =>
        from cartera in operaciones.AsignacionesCartera
        join operacion in operaciones.AsignacionesOperacion
            on cartera.AsignacionOperacionId equals operacion.Id
        where cartera.UsuarioId == usuarioId
              && cartera.Estado == EstadoAsignacion.Vigente
              && cartera.VigenciaDesde <= ahora
              && (cartera.VigenciaHasta == null || ahora < cartera.VigenciaHasta)
              // La raíz es el fallback del propietario sobre sí mismo, no un
              // contexto que se seleccione. Con las dos condiciones siguientes
              // una raíz bien formada ya queda fuera; esta la excluye aunque la
              // fila estuviera mal formada.
              && !operacion.EsRaiz
              && operacion.OperadorTenantId == tenantOrigenId
              // El Tenant de origen no se alcanza «por Operación»: una operación
              // interna (propietario = operador) lo devolvería otra vez, ya no
              // como origen.
              && operacion.PropietarioTenantId != tenantOrigenId
              && operacion.Estado == EstadoAsignacion.Vigente
              && operacion.VigenciaDesde <= ahora
              && (operacion.VigenciaHasta == null || ahora < operacion.VigenciaHasta)
        select new CarteraBajoOperacionVigente { Cartera = cartera, Operacion = operacion };

    /// <summary>
    /// Vía heredada: asignaciones de Operador Delegado del usuario sobre
    /// delegaciones activas y no caducadas (ver <c>DelegacionTenant.EstaVigente</c>,
    /// escrito aquí a mano porque el método de dominio no se traduce a SQL).
    /// </summary>
    public static IQueryable<AsignacionHeredadaVigente> AsignacionesHeredadasVigentes(
        ITenantsQueryContext tenants, Guid usuarioId, DateTime ahora) =>
        from asignacion in tenants.AsignacionesOperadorDelegado
        join delegacion in tenants.DelegacionesTenant on asignacion.DelegacionTenantId equals delegacion.Id
        where asignacion.UsuarioId == usuarioId
              && delegacion.Activa
              && (delegacion.ExpiraEnUtc == null || delegacion.ExpiraEnUtc > ahora)
        select new AsignacionHeredadaVigente { Asignacion = asignacion, Concesion = delegacion };

    /// <summary>
    /// La Asignación de Operación por la que el usuario puede abrir
    /// <paramref name="tenantId"/>, o <c>null</c>. Determinista (REC-136): si
    /// varias autorizan, la vigente más reciente y, a igualdad, la de menor Id.
    /// </summary>
    public static Task<Guid?> OperacionQueAutorizaAsync(
        IOperacionesQueryContext operaciones, Guid usuarioId, Guid tenantOrigenId, Guid tenantId,
        DateTime ahora, CancellationToken cancellationToken) =>
        CarterasPorOperacion(operaciones, usuarioId, tenantOrigenId, ahora)
            .Where(v => v.Operacion.PropietarioTenantId == tenantId)
            .OrderByDescending(v => v.Operacion.VigenciaDesde)
            .ThenBy(v => v.Operacion.Id)
            .Select(v => (Guid?)v.Operacion.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Roles que una cartera EXTERNA puede aportar como rol efectivo en el Tenant propietario.</summary>
    public static readonly IReadOnlyList<string> RolesDelegablesPorOperacion = ["CoordinadorCae", "GestorCae", "Consulta"];

    /// <summary>
    /// UNA sola definición del rol efectivo por la vía de Operación, para la
    /// operación ya elegida (<see cref="OperacionQueAutorizaAsync"/> o la del token):
    /// el par (operación, usuario) NO es único, así que entre las carteras vigentes
    /// con rol delegable manda la de menor Id (todas son universales: el reparto por
    /// Cliente empresarial está retirado, D-7). Lo usan
    /// <c>CurrentUserService</c> (rol efectivo) y <c>ObtenerClientesAutorizadosQuery</c>
    /// (Tenant por defecto, decisión 7 quater): ambos deben coincidir siempre.
    /// </summary>
    public static Task<string?> RolPorOperacionAsync(
        IOperacionesQueryContext operaciones, Guid usuarioId, Guid tenantOrigenId, Guid tenantId,
        Guid asignacionOperacionId, DateTime ahora, CancellationToken cancellationToken) =>
        CarterasPorOperacion(operaciones, usuarioId, tenantOrigenId, ahora)
            .Where(v => v.Operacion.Id == asignacionOperacionId
                        && v.Operacion.PropietarioTenantId == tenantId
                        && v.Cartera.Rol != null && RolesDelegablesPorOperacion.Contains(v.Cartera.Rol))
            .OrderBy(v => v.Cartera.Id)
            .Select(v => v.Cartera.Rol)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Revalidación de una selección que nombra una Asignación de Operación
    /// concreta: la operación sigue autorizando <paramref name="tenantId"/> a este
    /// usuario por el mismo predicado que la concedió, incluida la coherencia
    /// entre el Tenant y la operación del token.
    /// </summary>
    public static Task<bool> SigueAutorizadoPorOperacionAsync(
        IOperacionesQueryContext operaciones, Guid usuarioId, Guid tenantOrigenId, Guid tenantId,
        Guid asignacionOperacionId, DateTime ahora, CancellationToken cancellationToken) =>
        CarterasPorOperacion(operaciones, usuarioId, tenantOrigenId, ahora)
            .AnyAsync(v => v.Operacion.Id == asignacionOperacionId
                           && v.Operacion.PropietarioTenantId == tenantId,
                cancellationToken);

    /// <summary>La vía heredada autoriza <paramref name="tenantId"/> a este usuario.</summary>
    public static Task<bool> AutorizadoPorViaHeredadaAsync(
        ITenantsQueryContext tenants, Guid usuarioId, Guid tenantId, DateTime ahora,
        CancellationToken cancellationToken) =>
        AsignacionesHeredadasVigentes(tenants, usuarioId, ahora)
            .AnyAsync(v => v.Concesion.TenantClienteId == tenantId, cancellationToken);

    /// <summary>
    /// Las tres vías juntas —origen, Operación y heredada—, las mismas y en el mismo orden que
    /// <c>ClienteActivoEndpoints.CambiarAsync</c>, para quien solo necesita el sí o el no (el
    /// endpoint del logo del Tenant, invariante I2). No lee <c>Tenants</c>: se puede evaluar antes
    /// de tocar la fila del Tenant pedido.
    /// </summary>
    public static async Task<bool> EstaAutorizadoAsync(
        IOperacionesQueryContext operaciones, ITenantsQueryContext tenants, Guid usuarioId,
        Guid tenantOrigenId, Guid tenantId, DateTime ahora, CancellationToken cancellationToken) =>
        tenantId == tenantOrigenId
        || await OperacionQueAutorizaAsync(
            operaciones, usuarioId, tenantOrigenId, tenantId, ahora, cancellationToken) is not null
        || await AutorizadoPorViaHeredadaAsync(tenants, usuarioId, tenantId, ahora, cancellationToken);

    /// <summary>
    /// El Tenant de origen está gestionado por el usuario: tiene Asignación de
    /// Cartera vigente bajo una operación vigente del propio Tenant de origen
    /// sobre sí mismo (raíz o interna). Es lo que decide si el origen cuenta como
    /// Tenant de la cartera (contrato del selector, § 4.2.3 y decisión 5).
    /// No autoriza nada: el origen siempre está autorizado sobre sí mismo.
    /// </summary>
    public static Task<bool> OrigenGestionadoAsync(
        IOperacionesQueryContext operaciones, Guid usuarioId, Guid tenantOrigenId, DateTime ahora,
        CancellationToken cancellationToken) =>
        (from cartera in operaciones.AsignacionesCartera
         join operacion in operaciones.AsignacionesOperacion
             on cartera.AsignacionOperacionId equals operacion.Id
         where cartera.UsuarioId == usuarioId
               && cartera.Estado == EstadoAsignacion.Vigente
               && cartera.VigenciaDesde <= ahora
               && (cartera.VigenciaHasta == null || ahora < cartera.VigenciaHasta)
               && operacion.PropietarioTenantId == tenantOrigenId
               && operacion.OperadorTenantId == tenantOrigenId
               && operacion.Estado == EstadoAsignacion.Vigente
               && operacion.VigenciaDesde <= ahora
               && (operacion.VigenciaHasta == null || ahora < operacion.VigenciaHasta)
         select cartera.Id)
        .AnyAsync(cancellationToken);
}
