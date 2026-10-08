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
    /// Roles que una delegación o una cartera externa pueden dar en el Tenant
    /// propietario: solo de Operación (decisión del propietario, 2026-09-23;
    /// misma lista que <c>AsignacionesOperativasWriter</c>). Se vuelve a
    /// exigir al LEER porque pueden quedar filas anteriores a esa decisión.
    /// </summary>
    private static readonly string[] RolesDelegables = [Roles.CoordinadorCae, Roles.GestorCae, Roles.Consulta];

    /// <summary>
    /// El rol <b>efectivo en el contexto actual</b>, no el del claim de
    /// sesión. Mientras se opera un Delegated Workspace manda el rol de la
    /// <c>AsignacionOperadorDelegado</c> de esa delegación, que es lo que
    /// ADR-004 § 5.3 promete al decir que un mismo usuario puede ser GestorCae
    /// en un cliente y Consulta en otro.
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
    /// Decisión D-8 (2026-10-08): por la vía de Operación, el rol de la cartera
    /// es el suelo y un Encargo de administración vigente del Tenant
    /// propietario puede subir el techo hasta el perfil de Propiedad de la
    /// cuenta en su Tenant de origen. Ese cálculo vive entero en
    /// <see cref="TechoDeRolPorEncargo"/>; aquí solo se le pasa el rol de la
    /// sesión en origen. Este servicio no lee el rol de Identity para ello.
    /// </para>
    /// </summary>
    public async Task<string?> ObtenerRolEfectivoAsync() => (await ResolverRolEfectivoAsync()).Rol;

    /// <inheritdoc />
    public async Task<Guid?> EncargoQueElevaAsync() => (await ResolverRolEfectivoAsync()).EncargoAdministracionId;

    /// <summary>
    /// La operación seleccionada y el encargo que elevaba la última vez que se
    /// resolvió el rol efectivo para ella en este ámbito (petición o circuito).
    /// Solo la escribe la resolución de la selección, nunca la del fan-out: la
    /// auditoría describe el contexto en el que el usuario está, no cada Tenant
    /// que una consulta agregada visita.
    /// </summary>
    private (Guid AsignacionOperacionId, Guid? EncargoAdministracionId)? ultimaResolucionDeLaSeleccion;

    /// <inheritdoc />
    public Guid? EncargoDeLaUltimaResolucion(Guid asignacionOperacionId) =>
        ultimaResolucionDeLaSeleccion is { } ultima && ultima.AsignacionOperacionId == asignacionOperacionId
            ? ultima.EncargoAdministracionId
            : null;

    /// <summary>
    /// UNA sola resolución para el rol efectivo y para la señal «actúa por
    /// encargo»: las dos preguntas no pueden contestarse por caminos distintos.
    /// </summary>
    private async Task<RolEfectivoPorOperacion> ResolverRolEfectivoAsync()
    {
        var usuario = await ObtenerUsuarioAsync();
        var rolDeSesion = usuario?.FindFirst(ClaimTypes.Role)?.Value;

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
            return RolEfectivoPorOperacion.Ninguno;

        if (AmbitoTenantExplicito.TenantIdActual is { } tenantAmbito)
            return await ResolverRolParaAmbitoExplicitoAsync(tenantAmbito, RolDeSesionEnOrigen(usuario));

        // Sin selección no hay delegación en juego: el caso de todo usuario
        // que no es Operador Delegado de nadie, sin ninguna consulta extra.
        if (clienteActivoSeleccionado.TenantIdSeleccionado is not { } tenantSeleccionado)
            return new RolEfectivoPorOperacion(rolDeSesion, null);

        var usuarioId = await ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return RolEfectivoPorOperacion.Ninguno;

        // Vía nueva: el token identifica la operación exacta, así que el rol
        // sale de la cartera de ESE contexto — un lookup por clave, no una
        // búsqueda por tenant con FirstOrDefault, que elegía de forma no
        // determinista cuando un usuario tenía dos autorizaciones vivas sobre
        // el mismo tenant.
        if (clienteActivoSeleccionado.AsignacionOperacionIdSeleccionada is { } asignacionOperacionId)
        {
            var porOperacion = await ResolverRolViaOperacionAsync(
                tenantSeleccionado, usuarioId.Value, asignacionOperacionId, RolDeSesionEnOrigen(usuario));
            ultimaResolucionDeLaSeleccion = (asignacionOperacionId, porOperacion.EncargoAdministracionId);
            return porOperacion;
        }

        // La vía heredada (el acceso de soporte) nunca se eleva por encargo.
        return new RolEfectivoPorOperacion(
            await ResolverRolViaHeredadaAsync(tenantSeleccionado, usuarioId.Value), null);
    }

    /// <summary>
    /// Rol de la sesión en el Tenant de origen, tal como llegó en el token y
    /// después de las restricciones de sesión (<c>RestriccionLoginLocalClaimsTransformation</c>),
    /// pero ANTES de que <see cref="RolEfectivoDelWorkspaceMiddleware"/> lo
    /// sustituyera por el rol de la cartera del Workspace operativo derivado
    /// seleccionado. El middleware lo conserva en
    /// <see cref="RolEfectivoDelWorkspaceMiddleware.TipoClaimRolDeSesionOrigen"/>;
    /// sin sustitución, el claim de rol sigue siendo el de origen.
    ///
    /// <para>
    /// No es <see cref="ObtenerRolOrigenAsync"/> a propósito: aquí se decide
    /// alcance, y el rol de Identity se saltaría la restricción de login local
    /// (un GestorCae entrado con contraseña es Consulta en esta sesión).
    /// </para>
    ///
    /// <para>
    /// También es la mitad «de sesión» del techo por Encargo de administración
    /// (<see cref="TechoDeRolPorEncargo"/>, paso 5): la claim nunca eleva, solo
    /// puede impedirlo cuando no coincide con el perfil de Identity.
    /// </para>
    /// </summary>
    private static string? RolDeSesionEnOrigen(ClaimsPrincipal? usuario) =>
        usuario?.FindFirst(RolEfectivoDelWorkspaceMiddleware.TipoClaimRolDeSesionOrigen)?.Value
        ?? usuario?.FindFirst(ClaimTypes.Role)?.Value;

    /// <summary>
    /// Rol de la organización de origen, de Identity. Ver el contrato en
    /// <see cref="ICurrentUserService.ObtenerRolOrigenAsync"/>: no autoriza.
    /// Mismo motivo que <see cref="TieneDobleFactorActivoAsync"/> para resolver
    /// el <c>UserManager</c> dentro del método.
    /// </summary>
    public async Task<string?> ObtenerRolOrigenAsync()
    {
        var usuarioId = await ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return null;

        var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var usuario = await userManager.FindByIdAsync(usuarioId.Value.ToString());
        if (usuario is null) return null;

        // Uno por usuario (Usuarios.razor); el orden solo hace determinista
        // un estado que no debería existir.
        return (await userManager.GetRolesAsync(usuario)).Order(StringComparer.Ordinal).FirstOrDefault();
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
    private async Task<RolEfectivoPorOperacion> ResolverRolParaAmbitoExplicitoAsync(Guid tenantAmbito, string? rolDeSesionEnOrigen)
    {
        var usuarioId = await ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return RolEfectivoPorOperacion.Ninguno;

        var tenantOrigenId = await ObtenerTenantOrigenIdAsync();
        if (tenantOrigenId == tenantAmbito) return new RolEfectivoPorOperacion(rolDeSesionEnOrigen, null);

        // La misma decisión que /cuenta/cliente-activo: si una operación
        // autoriza el Tenant, el POST la embebe en el token y el rol sale SOLO
        // de ella, sin recurso a la vía heredada (una cartera con rol no
        // delegable da null aunque haya delegación); si ninguna autoriza, la
        // heredada. Un Tenant alcanzado por Operación entra en el fan-out
        // (ObtenerClientesAutorizadosQuery, lote 0 del selector de Tenant
        // beneficiario) con el mismo rol que tendría seleccionado: sin esto su
        // alcance sería cero y Mi trabajo y el Dashboard lo pintarían «sin
        // cartera» aunque la tenga.
        if (tenantOrigenId is null) return RolEfectivoPorOperacion.Ninguno;
        var operacionId = await TenantsBeneficiariosAutorizados.OperacionQueAutorizaAsync(
            serviceProvider.GetRequiredService<IOperacionesQueryContext>(),
            usuarioId.Value, tenantOrigenId.Value, tenantAmbito, DateTime.UtcNow, CancellationToken.None);

        return operacionId is not null
            ? await ResolverRolViaOperacionAsync(tenantAmbito, usuarioId.Value, operacionId.Value, rolDeSesionEnOrigen)
            : new RolEfectivoPorOperacion(await ResolverRolViaHeredadaAsync(tenantAmbito, usuarioId.Value), null);
    }

    /// <summary>
    /// Vía de Operación, con el predicado único de
    /// <see cref="TenantsBeneficiariosAutorizados"/>: la cartera vigente del
    /// usuario bajo una Asignación de Operación vigente, no raíz, de su Operador
    /// CAE de origen sobre <paramref name="tenantId"/>, bajo la operación
    /// <paramref name="asignacionOperacionId"/>: la del token de la selección o,
    /// en el fan-out, la que elegiría <c>/cuenta/cliente-activo</c>.
    ///
    /// <para>
    /// El par (operación, usuario) NO es único: los índices admiten una cartera
    /// universal y varias por cliente del mismo usuario bajo la misma operación,
    /// y el backfill genera esa combinación. Se ordena por ámbito universal
    /// primero y luego por Id: la universal es la que describe el rol en el
    /// Tenant, y el Id desempata de forma estable.
    /// </para>
    ///
    /// <para>
    /// Una cartera EXTERNA solo guarda roles de Operación (decisión del
    /// propietario, 2026-09-23): una fila heredada con Administrador o Dirección
    /// CAE no da ese rol en el Tenant propietario aunque siga vigente. Desde P8 la
    /// migración las cierra (Revocada); esta condición queda como defensa en
    /// profundidad. El predicado solo devuelve operaciones externas, así que la
    /// lista blanca aplica siempre.
    /// </para>
    ///
    /// <para>
    /// Sobre ese rol de cartera, <see cref="TechoDeRolPorEncargo"/> decide si un
    /// Encargo de administración vigente lo sube (decisión D-8, 2026-10-08).
    /// Se resuelve dentro del método por el mismo ciclo de DI que el resto de
    /// este servicio: depende de los contextos de consulta y de Identity.
    /// </para>
    /// </summary>
    private async Task<RolEfectivoPorOperacion> ResolverRolViaOperacionAsync(
        Guid tenantId, Guid usuarioId, Guid asignacionOperacionId, string? rolDeSesionEnOrigen)
    {
        if (await ObtenerTenantOrigenIdAsync() is not { } tenantOrigenId) return RolEfectivoPorOperacion.Ninguno;

        // La operación llega ya elegida (token o, en el fan-out, la misma que
        // embebería el POST). Elegirla dentro de esta misma expresión creaba una
        // expresión autorreferente que desbordaba la pila del funcletizador de EF.
        return await serviceProvider.GetRequiredService<TechoDeRolPorEncargo>().ResolverAsync(
            usuarioId, tenantOrigenId, tenantId, asignacionOperacionId, rolDeSesionEnOrigen,
            DateTime.UtcNow, CancellationToken.None);
    }

    /// <summary>
    /// Vía heredada: el acceso de soporte —y, hasta que termine su propia
    /// fase de migración, el resto de Delegated Workspaces— sigue montado
    /// sobre delegaciones. Se comprueba contra la delegación viva, no contra
    /// lo que dijera el token al emitirse — una revocación tiene que notarse
    /// aquí.
    /// </summary>
    private async Task<string?> ResolverRolViaHeredadaAsync(Guid tenantClienteId, Guid usuarioId)
    {
        var dbContext = serviceProvider.GetRequiredService<ITenantsQueryContext>();

        return await TenantsBeneficiariosAutorizados
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
