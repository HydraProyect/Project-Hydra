using System.Security.Claims;
using CaeManager.Application.Common;

namespace CaeManager.Web.Services;

/// <summary>
/// Sustituye los claims de rol por el rol <b>efectivo en el workspace
/// delegado activo</b> mientras se opera uno.
///
/// <para>
/// <b>El agujero que cierra.</b> El rol en el Tenant operado lo resuelve
/// <c>CurrentUserService.ObtenerRolEfectivoAsync</c>: por la vía de Asignación de
/// Operación es el perfil de origen, si la operación le sigue autorizando ese
/// Tenant (decisión Chris 2026-10-20, que deroga el «GestorCae en un cliente y
/// Consulta en otro» de ADR-004 § 5.3 para esa vía); por la vía heredada (acceso
/// de soporte), el rol de su fila delegada. Pero hay una segunda familia
/// de puertas que no consulta ese método jamás — los <c>[Authorize(Roles = …)]</c>
/// de páginas y endpoints, que preguntan directamente al
/// <c>ClaimsPrincipal</c>—, y ahí seguía contestando el rol del <b>tenant de
/// origen</b>. Un Administrador del tenant A, delegado como Consulta en el
/// tenant B, superaba con el claim de A las puertas de Administrador de B:
/// Configuración, Roles, Claves de API, Auditoría, Integraciones e
/// Importaciones del cliente que solo debía poder mirar. Escalada horizontal y
/// vertical a la vez, y precisamente el hallazgo N-5 que
/// <c>ObtenerRolEfectivoAsync</c> creía haber cerrado — lo cerró en el camino de
/// escritura, no en el de las puertas de página.
/// </para>
///
/// <para>
/// <b>Por qué sustituir y no quitar.</b> El plano 3 (sesión privilegiada de
/// plataforma) se resuelve quitando el rol entero, porque quien entra por
/// privilegio no es miembro del workspace y no debe tener ninguno
/// (<see cref="SesionPrivilegiadaSinRolDeNegocioMiddleware"/>). El plano 2 es
/// distinto: el operador delegado <b>sí</b> opera el Tenant, con el rol que le
/// corresponde en él. Quitarlo dejaría la delegación sin acceso a nada —el motivo
/// por el que el middleware del plano 3 conserva a propósito el claim aquí— y
/// conservarlo sin resolver es el agujero: aunque por la vía de Operación el rol
/// coincida con el de origen, solo vale mientras la operación siga autorizando
/// ese Tenant. La respuesta correcta es poner el rol que corresponde a este
/// Tenant, o ninguno.
/// </para>
///
/// <para>
/// <b>Por qué reutiliza <c>ObtenerRolEfectivoAsync</c> en vez de repetir la
/// consulta.</b> Porque dos resoluciones del mismo concepto divergen, y la
/// divergencia sería invisible: las puertas de página dirían una cosa y
/// <c>AutorizacionEscrituraBehavior</c> otra. Con una sola fuente, un cambio en
/// las reglas de cartera llega a las dos a la vez. Hereda además su fallo
/// cerrado: si la delegación se revocó mientras el token seguía vigente,
/// devuelve <c>null</c>, se retiran todos los claims de rol y las puertas
/// fallan cerradas — sin esperar a que
/// <see cref="RevalidacionClienteActivoMiddleware"/> invalide la selección más
/// adelante en el pipeline.
/// </para>
///
/// <para>
/// <b>El rol que pone es el perfil de origen</b> (decisión Chris 2026-10-20, que
/// deroga en el rol la D-8 del 2026-10-08). El Administrador del Operador CAE
/// externo es Administrador en todos los Tenants beneficiarios de la Asignación
/// de Operación de su organización, con todos sus poderes —también los de
/// Propiedad— y sin Encargo de administración; Dirección CAE, Coordinador CAE y
/// Consulta, igual con el suyo; el Gestor CAE, solo donde tiene cartera. El
/// encargo ya no eleva: <c>ObtenerRolEfectivoAsync</c> no devuelve encargo, así
/// que este middleware no deja el claim <see cref="TipoClaimEncargoAdministracion"/>
/// y <see cref="PaginasExcluidasDelEncargoAuthorizationHandler"/> no excluye nada
/// (deuda: la maquinaria del encargo se conserva sin señal). El claim que viniera
/// en el principal se sigue retirando antes de decidir.
/// </para>
///
/// <para>
/// <b>Posición y coste.</b> Entre <c>UseAuthentication</c> y
/// <c>UseAuthorization</c>, después del middleware del plano 3: es la ventana
/// en la que el principal ya existe y todavía no lo ha leído ninguna puerta.
/// Solo paga quien trae cookie de selección —los Operadores Delegados—, que ya
/// pagan una consulta equivalente en <see cref="RevalidacionClienteActivoMiddleware"/>.
/// Para todos los demás el middleware mira una cabecera y sigue.
/// </para>
///
/// <para>
/// El principal modificado alcanza al circuito de Blazor porque cambiar de
/// workspace exige una recarga completa del navegador (ver
/// <see cref="ClienteActivoSeleccionado"/> y <c>ClienteActivoEndpoints</c>), así
/// que el circuito nuevo se negocia sobre esta misma petición HTTP.
/// </para>
/// </summary>
public class RolEfectivoDelWorkspaceMiddleware(RequestDelegate siguiente)
{
    /// <summary>
    /// Claim en memoria (nunca se emite en la cookie) con el rol de la sesión
    /// en el Tenant de origen tal como estaba ANTES de sustituirlo; vacío si
    /// la sesión no traía ninguno. Lo lee <c>CurrentUserService</c> siempre que
    /// el Tenant que se opera es el de origen: cuando el fan-out multi-Tenant
    /// lo visita (decisión P7, 2026-09-23) y cuando no hay Tenant seleccionado
    /// —la selección se retiró o no llegó al circuito— (hallazgo del
    /// 2026-10-09). Ahí el rol efectivo es el de la sesión, nunca el de la
    /// cartera del Tenant propietario que estuvo seleccionado.
    /// No es un rol: <c>IsInRole</c> y <c>[Authorize(Roles = …)]</c> no lo ven.
    /// </summary>
    public const string TipoClaimRolDeSesionOrigen = "hydra:rol_sesion_origen";

    /// <summary>
    /// Claim en memoria que acompaña al rol cuando lo ha subido un Encargo de
    /// administración (decisión D-8, 2026-10-08). Su valor es el Id del encargo.
    /// No se persiste en la cookie: se calcula en cada petición, igual que el
    /// rol efectivo, y el circuito de Blazor lo conserva congelado junto a él.
    ///
    /// <para>
    /// <b>No concede nada: restringe.</b> Lo leen
    /// <see cref="PaginasExcluidasDelEncargoAuthorizationHandler"/>, para negar
    /// las páginas que el rol elevado abriría y el encargo no cubre, y
    /// <see cref="RevalidacionCircuitoActivoHandler"/>, para cortar el circuito
    /// cuando el encargo deja de elevar. Por eso se retira siempre antes de
    /// volver a ponerlo: un valor que llegara de fuera no debe sobrevivir.
    /// </para>
    /// </summary>
    public const string TipoClaimEncargoAdministracion = "talveg:encargo_administracion";

    /// <summary>
    /// Claim en memoria que acompaña a <see cref="TipoClaimEncargoAdministracion"/>
    /// cuando el encargo es <b>pleno</b>: el Tenant propietario no tiene
    /// Administrador propio, ni activo ni pendiente de activar (decisión de Chris, 2026-10-15). Su valor es el
    /// Id del encargo. Con él, <see cref="PaginasExcluidasDelEncargoAuthorizationHandler"/>
    /// no niega nada; <see cref="RevalidacionCircuitoActivoHandler"/> corta el
    /// circuito si el encargo deja de ser pleno. Mismo ciclo de vida que el otro:
    /// solo lo pone este middleware y se retira antes de decidir.
    /// </summary>
    public const string TipoClaimEncargoPleno = "talveg:encargo_administracion_pleno";

    public async Task InvokeAsync(
        HttpContext contexto,
        IClienteActivoSeleccionado clienteActivoSeleccionado,
        ICurrentUserService currentUserService,
        IEncargoDeAdministracionActual encargoDeAdministracionActual,
        ILogger<RolEfectivoDelWorkspaceMiddleware> logger)
    {
        // Antes de decidir nada: el claim del encargo solo lo pone este
        // middleware, en esta petición.
        RetirarClaimDelEncargo(contexto.User);

        if (DebeAjustarse(contexto, clienteActivoSeleccionado))
        {
            var rolDeSesion = contexto.User.FindFirst(ClaimTypes.Role)?.Value;
            var rolEfectivo = await currentUserService.ObtenerRolEfectivoAsync();

            // Misma resolución que acaba de dar el rol (no una segunda consulta):
            // el claim del encargo y el rol elevado salen de la misma decisión.
            var operacionSeleccionada = clienteActivoSeleccionado.AsignacionOperacionIdSeleccionada;
            var encargoQueEleva = operacionSeleccionada is { } operacionId
                ? encargoDeAdministracionActual.EncargoDeLaUltimaResolucion(operacionId)
                : null;
            var encargoPleno = operacionSeleccionada is { } operacionDelPleno
                               && encargoDeAdministracionActual.EncargoPlenoDeLaUltimaResolucion(operacionDelPleno);

            ConservarRolDeSesionOrigen(contexto.User, rolDeSesion);
            AplicarRol(contexto.User, rolEfectivo);
            if (rolEfectivo is not null && encargoQueEleva is { } encargoId)
                AplicarClaimDelEncargo(contexto.User, encargoId, encargoPleno);

            // Se registra porque hasta REC-189 esta sustitución no dejaba
            // rastro alguno: el propio hallazgo N-5 que este middleware
            // cierra —un claim de rol equivocado colando puertas de
            // [Authorize(Roles = …)]— sería indistinguible desde fuera de
            // una sustitución legítima si ninguna de las dos se registrara.
            //
            // Nivel distinto según lo que representa, no el mismo para las
            // dos ramas: operar un Tenant delegado con el rol que le
            // corresponde es el camino feliz de cualquier Operador Delegado
            // activo, en cada petición no estática — avisar ahí con Warning
            // fabricaría el defecto que REC-162 documentó (un aviso que
            // siempre suena se aprende a ignorar, y se ignorará el día que
            // tenga razón). Solo la retirada del rol —la delegación dejó de
            // valer— es la anomalía real, y es la que comparte nivel con
            // RevalidacionClienteActivoMiddleware para que los dos se lean
            // igual en el mismo artefacto cuando algo va mal.
            if (rolEfectivo is null)
            {
                logger.LogWarning(
                    "Rol efectivo del Workspace operativo derivado retirado en {Ruta}: {RolDeSesion} sin "
                    + "sustituto para el tenant {TenantSeleccionado}.",
                    ValoresParaLog.Sanear(contexto.Request.Path.ToUriComponent()),
                    ValoresParaLog.Sanear(rolDeSesion ?? "(ninguno)"),
                    clienteActivoSeleccionado.TenantIdSeleccionado);
            }
            else
            {
                logger.LogInformation(
                    "Rol efectivo del Workspace operativo derivado ajustado en {Ruta}: de {RolDeSesion} a "
                    + "{RolEfectivo} para el tenant {TenantSeleccionado}.",
                    ValoresParaLog.Sanear(contexto.Request.Path.ToUriComponent()),
                    ValoresParaLog.Sanear(rolDeSesion ?? "(ninguno)"),
                    ValoresParaLog.Sanear(rolEfectivo),
                    clienteActivoSeleccionado.TenantIdSeleccionado);
            }
        }

        await siguiente(contexto);
    }

    /// <summary>
    /// Servido por el middleware de ficheros estáticos y por el propio Blazor,
    /// nunca por una página o endpoint con <c>[Authorize(Roles = …)]</c>. No es
    /// una lista de conveniencia: este middleware corre <b>antes</b> del
    /// enrutado —tiene que hacerlo, o las puertas ya habrían contestado—, así
    /// que sin este corte cada JS y cada CSS de un Operador Delegado costaría
    /// una consulta a base de datos.
    ///
    /// <para>
    /// <c>/_blazor</c> queda fuera de la lista a propósito: por ahí se negocia
    /// el circuito, y es justo la petición en la que el principal corregido
    /// tiene que llegar. Los assets de <c>wwwroot</c> (<c>/css</c>, <c>/js</c>)
    /// tampoco se recortan, porque su prefijo no es distinguible del de una
    /// ruta de negocio sin adivinar — siguen costando lo mismo que ya cuestan
    /// en <see cref="RevalidacionClienteActivoMiddleware"/>, que hace una
    /// consulta equivalente y tampoco los filtra.
    /// </para>
    /// </summary>
    private static readonly string[] PrefijosSinAutorizacionDeRol = ["/_framework", "/_content"];

    private static bool DebeAjustarse(HttpContext contexto, IClienteActivoSeleccionado seleccion)
    {
        // Sin cookie no hay selección que valga, y no se descifra nada: el caso
        // de la inmensa mayoría de las peticiones.
        if (string.IsNullOrEmpty(contexto.Request.Cookies[ClienteActivoSeleccionado.NombreCookie]))
            return false;

        if (PrefijosSinAutorizacionDeRol.Any(
                p => contexto.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
            return false;

        // Un usuario sin autenticar no tiene rol que ajustar, y consultar la
        // base por él sería trabajo regalado a quien todavía no ha entrado.
        if (contexto.User.Identity?.IsAuthenticated != true)
            return false;

        // El plano 3 ya lo resolvió el middleware anterior quitando el rol
        // entero. Volver a tocarlo aquí solo podría devolvérselo.
        if (seleccion.SesionPrivilegiadaIdSeleccionada is not null)
            return false;

        // Un token manipulado, caducado o de otro usuario resuelve a null en la
        // abstracción, y entonces no hay workspace delegado: manda el claim de
        // sesión, que es el del tenant propio del usuario.
        return seleccion.TenantIdSeleccionado is not null;
    }

    /// <summary>
    /// Idempotente: si el principal ya pasó por aquí (no debería, es uno por
    /// petición), se conserva el primero, que es el único que es de origen:
    /// en una segunda pasada el claim de rol ya sería el de la cartera.
    ///
    /// <para>
    /// Se anota SIEMPRE que hay sustitución, también cuando la sesión no traía
    /// rol en origen (valor vacío): el claim es además la marca de «este
    /// principal está sustituido». Sin ella, quien busca el rol de sesión en
    /// origen caería al claim de rol y leería el de la cartera como si fuera
    /// de origen.
    /// </para>
    /// </summary>
    private static void ConservarRolDeSesionOrigen(ClaimsPrincipal principal, string? rolDeSesion)
    {
        if (principal.HasClaim(c => c.Type == TipoClaimRolDeSesionOrigen)) return;

        if (principal.Identity is ClaimsIdentity identidadPrincipal)
            identidadPrincipal.AddClaim(new Claim(TipoClaimRolDeSesionOrigen, rolDeSesion ?? string.Empty));
    }

    /// <summary>
    /// Deja el principal con exactamente un rol —el efectivo— o con ninguno.
    /// Se recorren todas las identidades, no solo la primera: un principal
    /// puede llevar varias, y dejar una con el rol viejo bastaría para que
    /// <c>IsInRole</c> siguiera contestando que sí.
    /// </summary>
    private static void RetirarClaimDelEncargo(ClaimsPrincipal principal)
    {
        foreach (var identidad in principal.Identities)
            foreach (var claim in identidad.Claims
                         .Where(c => c.Type is TipoClaimEncargoAdministracion or TipoClaimEncargoPleno).ToList())
                identidad.RemoveClaim(claim);
    }

    private static void AplicarClaimDelEncargo(ClaimsPrincipal principal, Guid encargoId, bool pleno)
    {
        if (principal.Identity is not ClaimsIdentity identidadPrincipal) return;

        identidadPrincipal.AddClaim(new Claim(TipoClaimEncargoAdministracion, encargoId.ToString()));
        if (pleno)
            identidadPrincipal.AddClaim(new Claim(TipoClaimEncargoPleno, encargoId.ToString()));
    }

    private static void AplicarRol(ClaimsPrincipal principal, string? rolEfectivo)
    {
        foreach (var identidad in principal.Identities)
            foreach (var claimRol in identidad.FindAll(identidad.RoleClaimType).ToList())
                identidad.RemoveClaim(claimRol);

        if (rolEfectivo is null) return;

        // Sobre la identidad principal, y con SU tipo de claim de rol: añadirlo
        // con ClaimTypes.Role a secas funcionaría por casualidad solo mientras
        // el esquema no lo cambie, y IsInRole compara contra RoleClaimType.
        if (principal.Identity is ClaimsIdentity identidadPrincipal)
            identidadPrincipal.AddClaim(new Claim(identidadPrincipal.RoleClaimType, rolEfectivo));
    }
}

public static class RolEfectivoDelWorkspaceMiddlewareExtensions
{
    /// <summary>
    /// Registrar entre <c>UseAuthentication</c> y <c>UseAuthorization</c>, y
    /// después de <c>UseSesionPrivilegiadaSinRolDeNegocio</c>. El orden no es
    /// preferencia: después de la segunda, las puertas de rol ya habrían
    /// contestado con el rol del tenant equivocado.
    /// </summary>
    public static IApplicationBuilder UseRolEfectivoDelWorkspace(this IApplicationBuilder app) =>
        app.UseMiddleware<RolEfectivoDelWorkspaceMiddleware>();
}
