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
/// <item><b>Operación</b> (<see cref="OperacionesQueAutorizan"/>): una Asignación de
/// Operación vigente, no raíz, cuyo Operador CAE es el Tenant de origen del usuario y
/// cuyo Tenant propietario es otro. Qué más hace falta lo decide el perfil del usuario
/// en su Tenant de origen (decisión Chris 2026-10-20): Administrador, Dirección CAE,
/// Coordinador CAE y Consulta entran por la operación de su organización, sin
/// Asignación de Cartera, si la operación es del Tenant entero; el Gestor CAE sigue
/// necesitando su Asignación de Cartera vigente bajo ella
/// (<see cref="CarterasPorOperacion"/>). Cualquier otro perfil, o ninguno, no entra.</item>
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
    /// Perfiles que, en su Tenant de origen, abren los Tenants beneficiarios que su
    /// organización opera <b>sin Asignación de Cartera</b> (decisión Chris 2026-10-20):
    /// basta la Asignación de Operación vigente del Tenant entero. Códigos de
    /// <c>Roles</c> repetidos a propósito: Application no referencia Infrastructure.Identity.
    /// </summary>
    public static readonly IReadOnlyList<string> RolesConAccesoPorOperacion =
        ["Administrador", "DireccionCae", "CoordinadorCae", "Consulta"];

    /// <summary>El único perfil que sigue acotado por su Asignación de Cartera.</summary>
    public const string RolAcotadoPorCartera = "GestorCae";

    /// <summary>
    /// Las Asignaciones de Operación vigentes, externas y no raíz de la organización del
    /// usuario (Operador CAE = su Tenant de origen) sobre otro Tenant, <b>del Tenant
    /// entero</b>: sin ninguna dimensión de ámbito. Una operación acotada no se abre sin
    /// cartera, porque los perfiles que entran por aquí tienen alcance de Tenant entero y
    /// ese alcance excedería el de la operación: fallo cerrado.
    /// </summary>
    public static IQueryable<AsignacionOperacion> OperacionesDelTenantEnteroDeLaOrganizacion(
        IOperacionesQueryContext operaciones, Guid tenantOrigenId, DateTime ahora) =>
        operaciones.AsignacionesOperacion.Where(operacion =>
            !operacion.EsRaiz
            && operacion.OperadorTenantId == tenantOrigenId
            && operacion.PropietarioTenantId != tenantOrigenId
            && operacion.Estado == EstadoAsignacion.Vigente
            && operacion.VigenciaDesde <= ahora
            && (operacion.VigenciaHasta == null || ahora < operacion.VigenciaHasta)
            && operacion.AmbitoRelacionClienteId == null
            && operacion.AmbitoCentroId == null
            && operacion.AmbitoTrabajadorId == null
            && operacion.AmbitoProyectoId == null);

    /// <summary>
    /// Las Asignaciones de Operación por las que este usuario puede abrir un Tenant
    /// beneficiario, según su perfil en el Tenant de origen (<paramref name="rolDeOrigen"/>,
    /// el rol de la sesión en origen, nunca el ya sustituido por el del Tenant operado):
    /// <list type="bullet">
    /// <item><see cref="RolesConAccesoPorOperacion"/>: las del Tenant entero de su
    /// organización, sin cartera.</item>
    /// <item><see cref="RolAcotadoPorCartera"/>: solo aquellas bajo las que tiene
    /// Asignación de Cartera vigente.</item>
    /// <item>Cualquier otro perfil, o ninguno: ninguna (fallo cerrado).</item>
    /// </list>
    /// </summary>
    public static IQueryable<AsignacionOperacion> OperacionesQueAutorizan(
        IOperacionesQueryContext operaciones, Guid usuarioId, Guid tenantOrigenId, string? rolDeOrigen,
        DateTime ahora)
    {
        if (rolDeOrigen is not null && RolesConAccesoPorOperacion.Contains(rolDeOrigen))
            return OperacionesDelTenantEnteroDeLaOrganizacion(operaciones, tenantOrigenId, ahora);

        if (rolDeOrigen == RolAcotadoPorCartera)
            return CarterasPorOperacion(operaciones, usuarioId, tenantOrigenId, ahora).Select(v => v.Operacion);

        return operaciones.AsignacionesOperacion.Where(_ => false);
    }

    /// <summary>
    /// La Asignación de Operación por la que el usuario puede abrir
    /// <paramref name="tenantId"/>, o <c>null</c>. Determinista (REC-136): si
    /// varias autorizan, la vigente más reciente y, a igualdad, la de menor Id.
    /// </summary>
    public static Task<Guid?> OperacionQueAutorizaAsync(
        IOperacionesQueryContext operaciones, Guid usuarioId, Guid tenantOrigenId, Guid tenantId,
        string? rolDeOrigen, DateTime ahora, CancellationToken cancellationToken) =>
        OperacionesQueAutorizan(operaciones, usuarioId, tenantOrigenId, rolDeOrigen, ahora)
            .Where(o => o.PropietarioTenantId == tenantId)
            .OrderByDescending(o => o.VigenciaDesde)
            .ThenBy(o => o.Id)
            .Select(o => (Guid?)o.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// <b>El rol efectivo en un Tenant beneficiario delegado</b> por la vía de Operación
    /// (decisión Chris 2026-10-20): es el perfil del usuario en su Tenant de origen
    /// (<paramref name="rolDeOrigen"/>), con todos sus poderes —también los de Propiedad—,
    /// siempre que <paramref name="asignacionOperacionId"/> le siga autorizando ese Tenant
    /// por <see cref="OperacionesQueAutorizan"/>. Si no, <c>null</c> (fallo cerrado).
    ///
    /// <para>
    /// El rol guardado en la Asignación de Cartera ya no decide: la cartera solo da acceso
    /// (al Gestor CAE) y alcance (<c>AlcanceDatosService</c>). Deroga la regla del
    /// 2026-09-23 («la Operación nunca concede roles de Propiedad») y el techo por Encargo
    /// de administración de ADR-011 § 2.7.7; el encargo queda sin efecto práctico en el
    /// rol (deuda: sus tablas, comandos y políticas siguen, sin consumidor en el rol).
    /// </para>
    ///
    /// <para>
    /// El alcance lo decide <paramref name="rolDeOrigen"/> (el perfil de origen) y el rol
    /// devuelto es <paramref name="rolDeSesionEnOrigen"/>. Coinciden salvo en una sesión
    /// limitada a Consulta por login local: ahí el perfil anterior a la restricción decide el
    /// alcance (un Gestor CAE sigue acotado por cartera) y el rol sigue siendo Consulta.
    /// </para>
    /// </summary>
    public static async Task<string?> RolEnTenantDelegadoAsync(
        IOperacionesQueryContext operaciones, Guid usuarioId, Guid tenantOrigenId, Guid tenantId,
        Guid asignacionOperacionId, string? rolDeOrigen, string? rolDeSesionEnOrigen, DateTime ahora,
        CancellationToken cancellationToken) =>
        await SigueAutorizadoPorOperacionAsync(
            operaciones, usuarioId, tenantOrigenId, tenantId, asignacionOperacionId, rolDeOrigen, ahora,
            cancellationToken)
            ? rolDeSesionEnOrigen
            : null;

    /// <summary>
    /// El rol que una persona del Operador CAE externo <b>ejerce</b> en el Tenant beneficiario de
    /// una delegación, para enseñarlo en la lista de personas de <c>/delegaciones</c>: el mismo
    /// criterio que <see cref="RolEnTenantDelegadoAsync"/> (decisión Chris 2026-10-20). Con un
    /// perfil de origen que da acceso por Operación (<see cref="RolesConAccesoPorOperacion"/> o
    /// <see cref="RolAcotadoPorCartera"/>), ese perfil: un Administrador del Operador CAE externo
    /// es Administrador en la Empresa gestionada aunque su cartera guarde «GestorCae».
    ///
    /// <para>
    /// El rol guardado (<paramref name="rolGuardado"/>) solo se enseña donde todavía decide o
    /// donde no hay nada mejor que decir: en una delegación de Soporte TALVEG (vía heredada,
    /// <c>CurrentUserService.ResolverRolViaHeredadaAsync</c>) y cuando el perfil de origen no se
    /// conoce o no da acceso por Operación. Es una etiqueta, no autoridad: ninguna puerta lo lee.
    /// </para>
    /// </summary>
    public static string RolEjercidoEnDelegacion(bool esSoporte, string rolGuardado, string? rolDeOrigen) =>
        !esSoporte && rolDeOrigen is not null
            && (RolesConAccesoPorOperacion.Contains(rolDeOrigen) || rolDeOrigen == RolAcotadoPorCartera)
            ? rolDeOrigen
            : rolGuardado;

    /// <summary>Roles que una cartera EXTERNA puede guardar (ya no deciden el rol efectivo).</summary>
    public static readonly IReadOnlyList<string> RolesDelegablesPorOperacion = ["CoordinadorCae", "GestorCae", "Consulta"];

    /// <summary>
    /// <b>Deuda (decisión Chris 2026-10-20):</b> el rol guardado en la cartera ya no es el rol
    /// efectivo (ver <see cref="RolEnTenantDelegadoAsync"/>); solo lo usa
    /// <see cref="TechoDeRolPorEncargo"/>, que se quedó sin consumidor en producción.
    ///
    /// UNA sola definición del <b>rol de cartera</b> por la vía de Operación, para la
    /// operación ya elegida (<see cref="OperacionQueAutorizaAsync"/> o la del token):
    /// el par (operación, usuario) NO es único (los índices solo garantizan una universal
    /// vigente), así que entre las carteras vigentes con rol delegable manda la de menor
    /// Id: determinista. Lo usan <see cref="TechoDeRolPorEncargo"/> (de donde
    /// <c>CurrentUserService</c> saca el rol efectivo) y <c>ObtenerClientesAutorizadosQuery</c>
    /// (Tenant por defecto, decisión 7 quater): los dos parten de la misma cartera.
    ///
    /// <para>
    /// El rol de cartera ya no es siempre el rol efectivo (decisión D-8, 2026-10-08): con
    /// Encargo de administración vigente, <see cref="TechoDeRolPorEncargo"/> puede subirlo
    /// al perfil de Propiedad que la persona tiene en su Tenant de origen. Sin cartera no
    /// hay nada que subir: lo que devuelve este método sigue siendo la condición de acceso.
    /// El Tenant por defecto se decide con el rol de cartera, no con el elevado.
    /// </para>
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
    /// El usuario tiene, bajo esa operación, una Asignación de Cartera vigente <b>del Tenant
    /// entero</b> con uno de <paramref name="roles"/>: cartera universal bajo una operación
    /// universal, es decir, ámbito efectivo sin ninguna dimensión concreta.
    ///
    /// <para>
    /// Es la condición que <see cref="TechoDeRolPorEncargo"/> exige antes de subir el rol: un rol
    /// de Propiedad tiene alcance total sin consultar carteras, así que elevar a quien solo tiene
    /// una cartera parcial convertiría un ámbito acotado en el Tenant entero. Se pregunta por la
    /// cartera que da el ámbito, no por la que da el rol (<see cref="RolPorOperacionAsync"/>
    /// elige la de menor Id): con una cartera parcial de gestión y otra universal de Consulta no
    /// hay ninguna que dé a la vez gestión y Tenant entero, y no se eleva.
    /// </para>
    ///
    /// <para>
    /// La universalidad se decide en memoria con <see cref="AmbitoAsignacion.EsUniversal"/>, la
    /// única definición del dominio, y no repitiendo aquí sus cuatro columnas: son las carteras
    /// de UNA persona bajo UNA operación (un puñado de filas) y así una dimensión nueva del
    /// ámbito no deja esta comprobación atrás.
    /// </para>
    /// </summary>
    public static async Task<bool> TieneCarteraDelTenantEnteroAsync(
        IOperacionesQueryContext operaciones, Guid usuarioId, Guid tenantOrigenId, Guid tenantId,
        Guid asignacionOperacionId, IReadOnlyList<string> roles, DateTime ahora,
        CancellationToken cancellationToken)
    {
        var carteras = await CarterasPorOperacion(operaciones, usuarioId, tenantOrigenId, ahora)
            .Where(v => v.Operacion.Id == asignacionOperacionId
                        && v.Operacion.PropietarioTenantId == tenantId
                        && v.Cartera.Rol != null && roles.Contains(v.Cartera.Rol))
            .ToListAsync(cancellationToken);

        return carteras.Any(v => v.Cartera.Ambito.EsUniversal && v.Operacion.Ambito.EsUniversal);
    }

    /// <summary>
    /// Revalidación de una selección que nombra una Asignación de Operación
    /// concreta: la operación sigue autorizando <paramref name="tenantId"/> a este
    /// usuario por el mismo predicado que la concedió, incluida la coherencia
    /// entre el Tenant y la operación del token.
    /// </summary>
    public static Task<bool> SigueAutorizadoPorOperacionAsync(
        IOperacionesQueryContext operaciones, Guid usuarioId, Guid tenantOrigenId, Guid tenantId,
        Guid asignacionOperacionId, string? rolDeOrigen, DateTime ahora, CancellationToken cancellationToken) =>
        OperacionesQueAutorizan(operaciones, usuarioId, tenantOrigenId, rolDeOrigen, ahora)
            .AnyAsync(o => o.Id == asignacionOperacionId && o.PropietarioTenantId == tenantId,
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
        Guid tenantOrigenId, Guid tenantId, string? rolDeOrigen, DateTime ahora, CancellationToken cancellationToken) =>
        tenantId == tenantOrigenId
        || await OperacionQueAutorizaAsync(
            operaciones, usuarioId, tenantOrigenId, tenantId, rolDeOrigen, ahora, cancellationToken) is not null
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
