using CaeManager.Application.Common;
using System.Security.Claims;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Web.Services;

/// <summary>
/// El <see cref="IServiceProvider"/> no es pereza de diseño: es lo que rompe
/// un ciclo de DI real. <c>CaeManagerDbContext</c> monta
/// <c>AuditoriaInterceptor</c>, que depende de este mismo servicio, así que
/// inyectar el contexto por constructor deja el grafo sin resolver y la
/// aplicación no arranca. Resolverlo dentro del método funciona porque el
/// único punto que necesita base de datos —el rol de una delegación y, sobre
/// él, el techo por Encargo de administración— nunca se llama desde el
/// interceptor, que solo pide el Id de usuario y la última resolución ya
/// hecha (<see cref="EncargoDeLaUltimaResolucion"/>) y no toca la base de datos.
/// </summary>
public class CurrentUserService(
    AuthenticationStateProvider authenticationStateProvider,
    IHttpContextAccessor httpContextAccessor,
    IClienteActivoSeleccionado clienteActivoSeleccionado,
    IServiceProvider serviceProvider) : ICurrentUserService, IEncargoDeAdministracionActual
{
    public async Task<Guid?> ObtenerUsuarioActualIdAsync()
    {
        var usuario = await ObtenerUsuarioAsync();
        if (usuario is null) return null;

        var valorClaim = usuario.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return Guid.TryParse(valorClaim, out var usuarioId) ? usuarioId : null;
    }

    /// <summary>
    /// Roles que una delegación heredada (acceso de soporte) puede dar en el Tenant
    /// propietario: solo de Operación (decisión del propietario, 2026-09-23;
    /// misma lista que <c>AsignacionesOperativasWriter</c>). Se vuelve a
    /// exigir al LEER porque pueden quedar filas anteriores a esa decisión. No
    /// aplica a la vía de Operación, donde rige el perfil de origen (decisión Chris
    /// 2026-10-20).
    /// </summary>
    private static readonly string[] RolesDelegables = [Roles.CoordinadorCae, Roles.GestorCae, Roles.Consulta];

    /// <summary>
    /// El rol <b>efectivo en el contexto actual</b>, no el del claim de
    /// sesión. Mientras se opera un Tenant beneficiario delegado por Asignación de
    /// Operación, el rol efectivo es el perfil de la sesión en el Tenant de origen,
    /// el mismo en todos los Tenants que la organización opera y con todos sus
    /// poderes (decisión Chris 2026-10-20, que deroga el «GestorCae en un cliente
    /// y Consulta en otro» de ADR-004 § 5.3 para esta vía); por la vía heredada
    /// (acceso de soporte) sigue mandando el rol de la <c>AsignacionOperadorDelegado</c>,
    /// acotado en ambas vías por el rol que dejó la restricción de login local, si la hay.
    ///
    /// Antes se devolvía siempre el claim, así que el rol de la asignación se
    /// guardaba y no se leía jamás: un operador asignado como Consulta sobre
    /// el tenant B, pero Administrador en el suyo, escribía en B con
    /// privilegios que nadie le había dado ahí (hallazgo N-5 de
    /// Project-Hydra-Negocio/seguridad/INFORME-AUDITORIA-2.md).
    ///
    /// Fallo cerrado: si se está operando un workspace delegado y no aparece
    /// asignación viva —porque la delegación se revocó mientras el token de
    /// selección seguía vigente— devuelve null, y ningún rol es peor que
    /// cualquier rol. Por eso <c>AutorizacionEscrituraBehavior</c> decide por
    /// lista blanca: con lista negra, "sin rol" habría dejado escribir.
    ///
    /// <para>
    /// <see cref="AmbitoTenantExplicito.TenantIdActual"/> tiene prioridad sobre
    /// la selección de workspace, con la misma precedencia que
    /// <c>TenantActual.TenantId</c> (mismo <c>AsyncLocal</c>): el fan-out
    /// multi-tenant de <c>ObtenerKpisGlobalesQuery</c>/<c>ObtenerDashboardEjecutivoQuery</c>
    /// visita, dentro del mismo ámbito de DI, tenants que no son ni el de
    /// origen ni el workspace seleccionado en la UI. Sin esto, cada vuelta del
    /// bucle recibía el mismo rol —el de la sesión, o el del workspace que
    /// sí estuviera activo— en vez del rol efectivo en CADA Cliente Delegante
    /// visitado: un Administrador de la consultora calculaba acceso total
    /// también para sus clientes delegantes, saltándose la Asignación de
    /// Cartera de su rol real ahí (hallazgo Codex 2026-09-11).
    /// </para>
    ///
    /// <para>
    /// Decisión P7 (2026-09-23): se llamaba <c>ObtenerRolActualAsync</c>, que
    /// queda como alias obsoleto. El rol de la organización de origen es otra
    /// API, <see cref="ObtenerRolOrigenAsync"/>, y no vale para autorizar.
    /// </para>
    ///
    /// <para>
    /// Decisión Chris 2026-10-20 (deroga la D-8 del 2026-10-08 en el rol): por la
    /// vía de Operación ya no hay rol de cartera ni techo por Encargo de
    /// administración. El rol es el de la sesión en origen, si la operación le
    /// sigue autorizando el Tenant
    /// (<see cref="TenantsBeneficiariosAutorizados.RolEnTenantDelegadoAsync"/>).
    /// Este servicio no lee el rol de Identity para ello.
    /// </para>
    /// </summary>
    public async Task<string?> ObtenerRolEfectivoAsync() => (await ResolverRolEfectivoAsync()).Rol;

    /// <inheritdoc />
    public async Task<string?> ObtenerPerfilQueDecideAlcanceAsync() => PerfilQueDecideAlcance(await ObtenerUsuarioAsync());

    /// <inheritdoc />
    public async Task<string?> ObtenerPerfilAntesDeRestringirAsync() =>
        (await ObtenerUsuarioAsync())?.FindFirst(RestriccionLoginLocalClaimsTransformation.TipoClaimPerfilAntesDeRestringir)
            is { Value.Length: > 0 } antes ? antes.Value : null;

    /// <inheritdoc />
    public async Task<string?> ObtenerRolDeAsignacionHeredadaAsync() =>
        (await ResolverRolSegunContextoAsync(await ObtenerUsuarioAsync())).RolHeredado;

    /// <inheritdoc />
    public async Task<bool> OperaPorAsignacionDeOperacionAsync() =>
        await ResolverRolEfectivoAsync() is { PorOperacion: true, Rol: not null };

    /// <inheritdoc />
    public async Task<Guid?> EncargoQueElevaAsync() => (await ResolverRolEfectivoAsync()).EncargoAdministracionId;

    /// <inheritdoc />
    public async Task<Guid?> EncargoPlenoAsync() =>
        await ResolverRolEfectivoAsync() is { EncargoPleno: true, EncargoAdministracionId: { } encargoId }
            ? encargoId
            : null;

    /// <summary>
    /// La operación seleccionada y el encargo que elevaba la última vez que se
    /// resolvió el rol efectivo para ella en este ámbito (petición o circuito).
    /// Solo la escribe la resolución de la selección, nunca la del fan-out: la
    /// auditoría describe el contexto en el que el usuario está, no cada Tenant
    /// que una consulta agregada visita.
    /// </summary>
    private (Guid AsignacionOperacionId, Guid? EncargoAdministracionId, bool EncargoPleno)? ultimaResolucionDeLaSeleccion;

    /// <inheritdoc />
    public Guid? EncargoDeLaUltimaResolucion(Guid asignacionOperacionId) =>
        ultimaResolucionDeLaSeleccion is { } ultima && ultima.AsignacionOperacionId == asignacionOperacionId
            ? ultima.EncargoAdministracionId
            : null;

    /// <inheritdoc />
    public bool EncargoPlenoDeLaUltimaResolucion(Guid asignacionOperacionId) =>
        ultimaResolucionDeLaSeleccion is { } ultima
        && ultima.AsignacionOperacionId == asignacionOperacionId
        && ultima.EncargoAdministracionId is not null
        && ultima.EncargoPleno;

    /// <summary>
    /// UNA sola resolución para el rol efectivo y para la señal «actúa por
    /// encargo»: las dos preguntas no pueden contestarse por caminos distintos.
    /// </summary>
    private async Task<RolEfectivoPorOperacion> ResolverRolEfectivoAsync()
    {
        var usuario = await ObtenerUsuarioAsync();

        var (resolucion, rolHeredado) = await ResolverRolSegunContextoAsync(usuario);
        var techo = RolDeSesionEnOrigen(usuario);

        // La asignación autoriza el Tenant propietario; no puede devolver la escritura que quitó el login
        // local. El techo es el rol de sesión ya restringido, conservado antes de sustituir los
        // claims: el perfil anterior solo decide el ámbito efectivo, nunca autoriza un rol.
        return resolucion.Rol is not null
               && usuario?.HasClaim(c => c.Type == RestriccionLoginLocalClaimsTransformation.TipoClaimPerfilAntesDeRestringir) == true
            ? resolucion with { Rol = rolHeredado is not null && techo != Roles.Consulta ? null : techo }
            : resolucion;
    }

    private async Task<(RolEfectivoPorOperacion Efectivo, string? RolHeredado)> ResolverRolSegunContextoAsync(ClaimsPrincipal? usuario)
    {
        // Una sesión privilegiada de plataforma no tiene rol de negocio, y
        // decirlo aquí explícitamente es justamente el punto (ADR-011 § 4bis.3):
        // las tres capas de autorización son distintas, y un técnico de soporte
        // entra por la del privilegio de plataforma sin ser jamás miembro del
        // workspace que visita — sin rol CAE y sin cartera. Devolver el claim de
        // su tenant de plataforma le daría dentro del tenant visitado el rol que
        // tiene en el suyo: el hallazgo N-5 en su versión más grave.
        //
        // Se decide con el token, sin consultar la base: negar de más siempre es
        // seguro, y quien decide si la sesión vale de verdad es
        // ISesionPrivilegiadaActual, que sí revalida. Aquí basta con saber que
        // este contexto no es de negocio.
        if (clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada is not null)
            return (RolEfectivoPorOperacion.Ninguno, null);

        if (AmbitoTenantExplicito.TenantIdActual is { } tenantAmbito)
            return await ResolverRolParaAmbitoExplicitoAsync(tenantAmbito, RolDeSesionEnOrigen(usuario));

        // Sin Tenant seleccionado se opera el Tenant de origen (TenantActual), y
        // ahí el rol es el de la sesión en origen, sin ninguna consulta extra.
        // No es «el claim de rol tal cual»: el principal de un circuito puede
        // llegar ya sustituido por RolEfectivoDelWorkspaceMiddleware y perder
        // después la selección —RevalidacionCircuitoActivoHandler la retira al
        // caducar o revocarse la Asignación de Cartera, y un ámbito que la lee
        // sin HttpContext la memoiza nula—. Devolver entonces el claim
        // autorizaba en el Tenant de origen con el rol que la cartera concede en
        // OTRO Tenant (hallazgo del 2026-10-09). Para quien nunca seleccionó un
        // Tenant no cambia nada: sin sustitución, el rol de sesión en origen es
        // el propio claim.
        if (clienteActivoSeleccionado.TenantIdSeleccionado is not { } tenantSeleccionado)
            return (new RolEfectivoPorOperacion(RolDeSesionEnOrigen(usuario), null), null);

        var usuarioId = await ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return (RolEfectivoPorOperacion.Ninguno, null);

        // Vía nueva: el token identifica la operación exacta, así que el rol
        // sale de la cartera de ESE contexto — un lookup por clave, no una
        // búsqueda por tenant con FirstOrDefault, que elegía de forma no
        // determinista cuando un usuario tenía dos autorizaciones vivas sobre
        // el mismo tenant.
        if (clienteActivoSeleccionado.AsignacionOperacionIdSeleccionada is { } asignacionOperacionId)
        {
            var porOperacion = await ResolverRolViaOperacionAsync(
                tenantSeleccionado, usuarioId.Value, asignacionOperacionId, RolDeSesionEnOrigen(usuario));
            ultimaResolucionDeLaSeleccion = (asignacionOperacionId, porOperacion.EncargoAdministracionId, porOperacion.EncargoPleno);
            return (porOperacion, null);
        }

        // La vía heredada (el acceso de soporte) nunca se eleva por encargo.
        return await ResolverRolViaHeredadaAsync(tenantSeleccionado, usuarioId.Value);
    }

    /// <summary>
    /// Rol de la sesión en el Tenant de origen, tal como llegó en el token y
    /// después de las restricciones de sesión (<c>RestriccionLoginLocalClaimsTransformation</c>),
    /// pero ANTES de que <see cref="RolEfectivoDelWorkspaceMiddleware"/> lo
    /// sustituyera por el rol de la cartera del Workspace operativo derivado
    /// seleccionado. El middleware lo conserva en
    /// <see cref="RolEfectivoDelWorkspaceMiddleware.TipoClaimRolDeSesionOrigen"/>;
    /// sin sustitución, el claim de rol sigue siendo el de origen. Si el claim
    /// conservado existe, manda aunque esté vacío (la sesión no traía rol en
    /// origen): caer entonces al claim de rol devolvería el de la cartera.
    ///
    /// <para>
    /// No es <see cref="ObtenerRolOrigenAsync"/> a propósito: aquí se decide
    /// alcance, y el rol de Identity se saltaría la restricción de login local
    /// (un GestorCae entrado con contraseña es Consulta en esta sesión).
    /// </para>
    ///
    /// <para>
    /// Es además el rol efectivo en los Tenants beneficiarios delegados por la vía de
    /// Operación (decisión Chris 2026-10-20).
    /// </para>
    /// </summary>
    private static string? RolDeSesionEnOrigen(ClaimsPrincipal? usuario) =>
        usuario?.FindFirst(RolEfectivoDelWorkspaceMiddleware.TipoClaimRolDeSesionOrigen) is { } conservado
            ? (conservado.Value.Length > 0 ? conservado.Value : null)
            : usuario?.FindFirst(ClaimTypes.Role)?.Value;

    /// <summary>
    /// El perfil de origen que decide qué Tenants beneficiarios delegados alcanza la sesión
    /// (decisión Chris 2026-10-20): el rol de sesión en origen, salvo que la sesión esté
    /// limitada a Consulta por login local; entonces, el perfil anterior a esa restricción
    /// (<see cref="RestriccionLoginLocalClaimsTransformation.TipoClaimPerfilAntesDeRestringir"/>),
    /// para que la restricción nunca amplíe el alcance de un Gestor CAE de su cartera a toda la
    /// operación. No es rol efectivo: el rol sigue siendo <see cref="RolDeSesionEnOrigen"/>.
    ///
    /// <para>
    /// Si el claim de la restricción existe, manda aunque esté vacío: la cuenta no traía rol
    /// conocido y el perfil es <c>null</c> (fallo cerrado), nunca el rol de sesión.
    /// Sin restricción, el rol de sesión en origen.
    /// </para>
    /// </summary>
    private static string? PerfilQueDecideAlcance(ClaimsPrincipal? usuario) =>
        usuario?.FindFirst(RestriccionLoginLocalClaimsTransformation.TipoClaimPerfilAntesDeRestringir) is { } antes
            ? (antes.Value.Length > 0 ? antes.Value : null)
            : RolDeSesionEnOrigen(usuario);

    /// <summary>
    /// Rol de la organización de origen, de Identity. Ver el contrato en
    /// <see cref="ICurrentUserService.ObtenerRolOrigenAsync"/>: no autoriza.
    /// Proyecta la cuenta (<see cref="ILecturaCuentaPropia"/>): en una Sesión
    /// Privilegiada, materializarla leería sus credenciales con <c>cae_app_soporte</c>.
    /// Mismo motivo que <see cref="TieneDobleFactorActivoAsync"/> para resolver
    /// la lectura dentro del método.
    /// </summary>
    public async Task<string?> ObtenerRolOrigenAsync()
    {
        var usuarioId = await ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return null;

        var cuenta = await serviceProvider.GetRequiredService<ILecturaCuentaPropia>().ObtenerAsync(usuarioId.Value);

        // Uno por usuario (Usuarios.razor); el orden solo hace determinista
        // un estado que no debería existir.
        return cuenta?.Roles.Order(StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>
    /// Rol efectivo para un tenant fijado por <see cref="AmbitoTenantExplicito"/>
    /// (fan-out multi-tenant). Si el ámbito coincide con el tenant de origen,
    /// es simplemente "mirar el propio tenant" y el rol es el de la sesión en
    /// origen — no hay <c>DelegacionTenant</c> de un tenant a sí mismo que
    /// consultar. Ese rol llega en <paramref name="rolDeSesionEnOrigen"/> y no
    /// del claim de rol a secas: con un Workspace operativo derivado
    /// seleccionado, el claim ya es el de la cartera del Tenant propietario
    /// (defecto de la decisión P7, 2026-09-23).
    ///
    /// Fuera del origen resuelve por las mismas vías y el mismo predicado
    /// (<see cref="TenantsBeneficiariosAutorizados"/>) con los que
    /// <c>ObtenerClientesAutorizadosQuery</c> enumera qué Tenants entran en el
    /// fan-out: la de Operación y la heredada. Un Tenant de la lista siempre
    /// tiene aquí el rol que tendría seleccionado.
    /// </summary>
    private async Task<(RolEfectivoPorOperacion Efectivo, string? RolHeredado)> ResolverRolParaAmbitoExplicitoAsync(Guid tenantAmbito, string? rolDeSesionEnOrigen)
    {
        var usuarioId = await ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return (RolEfectivoPorOperacion.Ninguno, null);

        var tenantOrigenId = await ObtenerTenantOrigenIdAsync();
        if (tenantOrigenId == tenantAmbito) return (new RolEfectivoPorOperacion(rolDeSesionEnOrigen, null), null);

        // La misma decisión que /cuenta/cliente-activo: si una operación
        // autoriza el Tenant, el POST la embebe en el token y el rol sale SOLO
        // de ella, sin recurso a la vía heredada (una cartera con rol no
        // delegable da null aunque haya delegación); si ninguna autoriza, la
        // heredada. Un Tenant alcanzado por Operación entra en el fan-out
        // (ObtenerClientesAutorizadosQuery, lote 0 del selector de Tenant
        // beneficiario) con el mismo rol que tendría seleccionado: sin esto su
        // alcance sería cero y Mi trabajo y el Dashboard lo pintarían «sin
        // cartera» aunque la tenga.
        if (tenantOrigenId is null) return (RolEfectivoPorOperacion.Ninguno, null);
        var operacionId = await TenantsBeneficiariosAutorizados.OperacionQueAutorizaAsync(
            serviceProvider.GetRequiredService<IOperacionesQueryContext>(),
            usuarioId.Value, tenantOrigenId.Value, tenantAmbito, PerfilQueDecideAlcance(await ObtenerUsuarioAsync()),
            DateTime.UtcNow, CancellationToken.None);

        return operacionId is not null
            ? (await ResolverRolViaOperacionAsync(tenantAmbito, usuarioId.Value, operacionId.Value, rolDeSesionEnOrigen), null)
            : await ResolverRolViaHeredadaAsync(tenantAmbito, usuarioId.Value);
    }

    /// <summary>
    /// Vía de Operación (decisión Chris 2026-10-20): el rol efectivo en
    /// <paramref name="tenantId"/> es el perfil de la sesión en origen, con todos
    /// sus poderes, mientras <paramref name="asignacionOperacionId"/> —la del token
    /// de la selección o, en el fan-out, la que elegiría <c>/cuenta/cliente-activo</c>—
    /// le siga autorizando ese Tenant por el predicado único
    /// (<see cref="TenantsBeneficiariosAutorizados.RolEnTenantDelegadoAsync"/>):
    /// operación del Tenant entero de su organización para Administrador, Dirección
    /// CAE, Coordinador CAE y Consulta; además cartera vigente para el Gestor CAE.
    /// El rol guardado en la cartera y el Encargo de administración ya no deciden.
    /// </summary>
    private async Task<RolEfectivoPorOperacion> ResolverRolViaOperacionAsync(
        Guid tenantId, Guid usuarioId, Guid asignacionOperacionId, string? rolDeSesionEnOrigen)
    {
        if (await ObtenerTenantOrigenIdAsync() is not { } tenantOrigenId) return RolEfectivoPorOperacion.Ninguno;

        var rol = await TenantsBeneficiariosAutorizados.RolEnTenantDelegadoAsync(
            serviceProvider.GetRequiredService<IOperacionesQueryContext>(),
            usuarioId, tenantOrigenId, tenantId, asignacionOperacionId,
            PerfilQueDecideAlcance(await ObtenerUsuarioAsync()), rolDeSesionEnOrigen,
            DateTime.UtcNow, CancellationToken.None);

        return new RolEfectivoPorOperacion(rol, null, PorOperacion: true);
    }

    /// <summary>
    /// Vía heredada: el acceso de soporte —y, hasta que termine su propia
    /// fase de migración, el resto de Delegated Workspaces— sigue montado
    /// sobre delegaciones. Se comprueba contra la delegación viva, no contra
    /// lo que dijera el token al emitirse — una revocación tiene que notarse
    /// aquí.
    /// </summary>
    private async Task<(RolEfectivoPorOperacion Efectivo, string? RolHeredado)> ResolverRolViaHeredadaAsync(Guid tenantClienteId, Guid usuarioId)
    {
        var dbContext = serviceProvider.GetRequiredService<ITenantsQueryContext>();

        var rol = await TenantsBeneficiariosAutorizados
            .AsignacionesHeredadasVigentes(dbContext, usuarioId, DateTime.UtcNow)
            .Where(v => v.Concesion.TenantClienteId == tenantClienteId
                        // Misma frontera que la vía de Operación: una asignación
                        // heredada con un rol de Propiedad no concede nada (falla
                        // cerrado). Desde P8 esas filas están revocadas y la vista
                        // AsignacionesOperadorDelegado ya no las devuelve; esta
                        // lista blanca queda como defensa en profundidad.
                        && RolesDelegables.Contains(v.Asignacion.Rol))
            .Select(v => v.Asignacion.Rol)
            .FirstOrDefaultAsync();

        return (new RolEfectivoPorOperacion(rol, null), rol);
    }

    public async Task<Guid?> ObtenerTenantOrigenIdAsync()
    {
        var usuario = await ObtenerUsuarioAsync();
        var valorClaim = usuario?.FindFirst(TenantClaimsPrincipalFactory.TipoClaimTenantId)?.Value;
        return Guid.TryParse(valorClaim, out var tenantId) ? tenantId : null;
    }

    // Mismo motivo que el IApplicationDbContext de ObtenerRolEfectivoAsync:
    // UserManager<ApplicationUser> depende en última instancia de
    // CaeManagerDbContext (vía UserStore), que monta AuditoriaInterceptor,
    // que depende de este mismo servicio — inyectarlo por constructor deja
    // el grafo sin resolver.
    public async Task<bool> TieneDobleFactorActivoAsync()
    {
        var usuarioId = await ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return false;

        // Consulta sin rastreo y no FindByIdAsync: en un circuito, FindByIdAsync
        // devuelve la cuenta ya rastreada sin refrescarla, y un 2FA restablecido
        // desde otro ámbito (RestablecerSegundoFactorCommand) seguiría leyéndose
        // como activo — y abriendo credenciales (P1-I1) — hasta cerrar el
        // circuito. Mismo defecto que SegundoFactorDeCuentasIdentity.CargarEnFrescoAsync.
        var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var id = usuarioId.Value;
        return await userManager.Users.AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => u.TwoFactorEnabled)
            .FirstOrDefaultAsync();
    }

    public async Task<bool> SuperoTotpEnSesionAsync() =>
        SignInManagerCuentaDesactivada.TotpEsReciente(await ObtenerUsuarioAsync(), DateTimeOffset.UtcNow);

    // Dentro de un circuito de Blazor, AuthenticationStateProvider ya trae el
    // ClaimsPrincipal correcto (capturado al negociar el circuito). Fuera de
    // uno — endpoints minimal API como GET /documentos/{id}/archivo, que no
    // tienen circuito pero sí HttpContext.User ya autenticado por la cookie
    // de Identity — hace falta el fallback a IHttpContextAccessor; si tampoco
    // hay HttpContext (migraciones/siembra al arrancar, jobs en segundo
    // plano), no hay usuario que auditar.
    private async Task<ClaimsPrincipal?> ObtenerUsuarioAsync()
    {
        try
        {
            var estado = await authenticationStateProvider.GetAuthenticationStateAsync();
            if (estado.User.Identity?.IsAuthenticated == true)
                return estado.User;
        }
        catch (InvalidOperationException)
        {
            // sin circuito de Blazor — se intenta el fallback de abajo.
        }

        // Un circuito cuya sesión ya no vale NO recupera identidad por el
        // HttpContext de la petición que lo abrió (ver ISesionDeCircuitoInvalidable).
        if (authenticationStateProvider is ISesionDeCircuitoInvalidable { SesionInvalidada: true })
            return null;

        var usuarioHttp = httpContextAccessor.HttpContext?.User;
        return usuarioHttp?.Identity?.IsAuthenticated == true ? usuarioHttp : null;
    }
}
