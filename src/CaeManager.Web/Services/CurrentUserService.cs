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
/// único punto que necesita base de datos —el rol de una delegación— nunca
/// se llama desde el interceptor, que solo pide el Id de usuario y no toca
/// la base de datos.
/// </summary>
public class CurrentUserService(
    AuthenticationStateProvider authenticationStateProvider,
    IHttpContextAccessor httpContextAccessor,
    IClienteActivoSeleccionado clienteActivoSeleccionado,
    IServiceProvider serviceProvider) : ICurrentUserService
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
    /// </summary>
    public async Task<string?> ObtenerRolEfectivoAsync()
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
            return null;

        if (AmbitoTenantExplicito.TenantIdActual is { } tenantAmbito)
            return await ResolverRolParaAmbitoExplicitoAsync(tenantAmbito, RolDeSesionEnOrigen(usuario));

        // Sin selección no hay delegación en juego: el caso de todo usuario
        // que no es Operador Delegado de nadie, sin ninguna consulta extra.
        if (clienteActivoSeleccionado.TenantIdSeleccionado is not { } tenantSeleccionado)
            return rolDeSesion;

        var usuarioId = await ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return null;

        // Vía nueva: el token identifica la operación exacta, así que el rol
        // sale de la cartera de ESE contexto — un lookup por clave, no una
        // búsqueda por tenant con FirstOrDefault, que elegía de forma no
        // determinista cuando un usuario tenía dos autorizaciones vivas sobre
        // el mismo tenant.
        if (clienteActivoSeleccionado.AsignacionOperacionIdSeleccionada is { } asignacionOperacionId)
            return await ResolverRolViaOperacionAsync(tenantSeleccionado, usuarioId.Value, asignacionOperacionId);

        return await ResolverRolViaHeredadaAsync(tenantSeleccionado, usuarioId.Value);
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
    private async Task<string?> ResolverRolParaAmbitoExplicitoAsync(Guid tenantAmbito, string? rolDeSesionEnOrigen)
    {
        var usuarioId = await ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return null;

        var tenantOrigenId = await ObtenerTenantOrigenIdAsync();
        if (tenantOrigenId == tenantAmbito) return rolDeSesionEnOrigen;

        // Mismo orden de vías que /cuenta/cliente-activo: primero la operación,
        // después la heredada. Un Tenant alcanzado por Operación entra en el
        // fan-out (ObtenerClientesAutorizadosQuery, lote 0 del selector de Tenant
        // beneficiario) y en su vuelta tiene el mismo rol que tendría el usuario
        // si lo seleccionara: sin esto su alcance sería cero y Mi trabajo y el
        // Dashboard lo pintarían «sin cartera» aunque la tenga.
        return await ResolverRolViaOperacionAsync(tenantAmbito, usuarioId.Value, asignacionOperacionId: null)
               ?? await ResolverRolViaHeredadaAsync(tenantAmbito, usuarioId.Value);
    }

    /// <summary>
    /// Vía de Operación, con el predicado único de
    /// <see cref="TenantsBeneficiariosAutorizados"/>: la cartera vigente del
    /// usuario bajo una Asignación de Operación vigente, no raíz, de su Operador
    /// CAE de origen sobre <paramref name="tenantId"/>. Con
    /// <paramref name="asignacionOperacionId"/> (selección del token) es un lookup
    /// por esa operación; sin él (fan-out), la operación que elegiría
    /// <c>/cuenta/cliente-activo</c>: la vigente más reciente y, a igualdad, la de
    /// menor Id.
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
    /// Una cartera EXTERNA solo aporta roles de Operación (decisión del
    /// propietario, 2026-09-23): una fila heredada con Administrador o Dirección
    /// CAE no da ese rol en el Tenant propietario aunque siga vigente. Desde P8 la
    /// migración las cierra (Revocada); esta condición queda como defensa en
    /// profundidad. El predicado solo devuelve operaciones externas, así que la
    /// lista blanca aplica siempre.
    /// </para>
    /// </summary>
    private async Task<string?> ResolverRolViaOperacionAsync(
        Guid tenantId, Guid usuarioId, Guid? asignacionOperacionId)
    {
        if (await ObtenerTenantOrigenIdAsync() is not { } tenantOrigenId) return null;

        var operaciones = serviceProvider.GetRequiredService<IOperacionesQueryContext>();
        var ahora = DateTime.UtcNow;

        // Sin operación en el token (fan-out), la misma que embebería el POST.
        // Dos consultas a propósito: componer la elección dentro de la misma
        // expresión que se filtra creaba una expresión autorreferente que
        // desbordaba la pila del funcletizador de EF.
        var operacionId = asignacionOperacionId
            ?? await TenantsBeneficiariosAutorizados.OperacionQueAutorizaAsync(
                operaciones, usuarioId, tenantOrigenId, tenantId, ahora, CancellationToken.None);
        if (operacionId is null) return null;

        return await TenantsBeneficiariosAutorizados
            .CarterasPorOperacion(operaciones, usuarioId, tenantOrigenId, ahora)
            .Where(v => v.Operacion.Id == operacionId.Value
                        && v.Operacion.PropietarioTenantId == tenantId
                        && v.Cartera.Rol != null && RolesDelegables.Contains(v.Cartera.Rol))
            .OrderBy(v => v.Cartera.AmbitoRelacionClienteId == null ? 0 : 1)
            .ThenBy(v => v.Cartera.Id)
            .Select(v => v.Cartera.Rol)
            .FirstOrDefaultAsync();
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
