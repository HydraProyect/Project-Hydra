namespace CaeManager.Application.Common;

/// <summary>
/// Abstrae la identidad del usuario autenticado frente a Infrastructure
/// (auditoría) y los handlers de Application. La implementación real vive en
/// Web, que es la única capa con acceso al contexto de autenticación de
/// Blazor Server.
/// </summary>
public interface ICurrentUserService
{
    Task<Guid?> ObtenerUsuarioActualIdAsync();

    /// <summary>
    /// Rol <b>efectivo</b> del usuario en el Tenant que se está operando ahora
    /// (el sistema asigna exactamente uno por usuario y contexto). Es el único
    /// rol que vale para autorizar escritura, middleware y la UI
    /// del Tenant actual:
    /// <list type="bullet">
    /// <item>sin Workspace operativo derivado, el rol de la sesión en el
    /// Tenant de origen (con las restricciones de sesión ya aplicadas, p. ej.
    /// login local);</item>
    /// <item>en un Workspace operativo derivado abierto por Asignación de
    /// Operación, el perfil de la sesión en el Tenant de origen, mientras la
    /// operación le siga autorizando ese Tenant (decisión Chris 2026-10-20: la
    /// cartera solo da acceso y alcance al Gestor CAE, no el rol); por la
    /// delegación heredada (acceso de soporte), el rol de su fila acotado por el techo de login local;</item>
    /// <item>bajo <c>AmbitoTenantExplicito</c> (fan-out multi-Tenant), el del
    /// Tenant visitado en cada vuelta — el de sesión si es el de origen;</item>
    /// <item>null bajo una Sesión Privilegiada de plataforma, sin sesión, o
    /// sin asignación viva (fallo cerrado).</item>
    /// </list>
    /// Decisión P7 (2026-09-23): nunca usar en su lugar el rol de origen de
    /// Identity (<see cref="ObtenerRolOrigenAsync"/>). Que por la vía de Operación
    /// coincida con el rol de la sesión en origen lo decide este método, que
    /// revalida la operación; no autoriza leerlo directamente.
    /// </summary>
    Task<string?> ObtenerRolEfectivoAsync();

    /// <summary>
    /// Rol del usuario <b>en su organización de origen</b>, leído en Identity
    /// (<c>AspNetUserRoles</c>) y no del claim de sesión, que dentro de un
    /// Workspace operativo derivado ya está sustituido por el rol efectivo.
    /// Sirve para identidad, informes y pertenencia a la organización (p. ej.
    /// a la del Operador CAE). <b>Nunca</b> para autorización ni alcance: no
    /// refleja la cartera del Tenant operado ni las restricciones de sesión
    /// (login local), y un trinquete de arquitectura lo prohíbe allí
    /// (<c>RolDeOrigenFueraDeAutorizacionTests</c>). Null sin usuario o sin
    /// rol asignado. No depende del Tenant operado ni de una Sesión
    /// Privilegiada: es un dato de identidad de la cuenta.
    /// </summary>
    Task<string?> ObtenerRolOrigenAsync();

    /// <summary>
    /// Perfil de la <b>sesión</b> en el Tenant de origen que decide qué Tenants beneficiarios
    /// delegados alcanza (decisión Chris 2026-10-20) y por qué vía entra el usuario
    /// (<c>TenantsBeneficiariosAutorizados.OperacionesQueAutorizan</c>): el rol del token antes
    /// de sustituirse por el del Tenant operado y, si la sesión quedó limitada a Consulta por
    /// login local, el perfil anterior a esa restricción, para que la restricción nunca amplíe
    /// el alcance (un Gestor CAE sigue acotado por cartera). No es rol efectivo. A diferencia de
    /// <see cref="ObtenerRolOrigenAsync"/>, sí vale para autorizar: es la sesión, no Identity.
    /// La implementación por defecto solo sirve a dobles de prueba sin Tenant delegado.
    /// </summary>
    Task<string?> ObtenerPerfilQueDecideAlcanceAsync() => ObtenerRolEfectivoAsync();

    /// <summary>
    /// El perfil que la sesión traía antes de quedar limitada a solo lectura por login local, o
    /// <c>null</c> si la sesión no está limitada (o no traía rol conocido). No es rol efectivo ni
    /// autoriza nada: solo permite que el alcance de datos siga siendo el del rol real, para que la
    /// restricción nunca lo amplíe (un Gestor CAE en Consulta seguiría viendo el Tenant entero).
    /// La implementación por defecto, sin restricción, solo sirve a dobles de prueba.
    /// </summary>
    Task<string?> ObtenerPerfilAntesDeRestringirAsync() => Task.FromResult<string?>(null);

    /// <summary>
    /// Rol de la asignación heredada vigente en el Tenant propietario del contexto, antes del
    /// techo de login local. Solo decide el ámbito efectivo de datos, nunca autoriza escritura.
    /// No es el perfil del Tenant de origen. Null por otras vías o sin asignación vigente.
    /// La implementación por defecto falla cerrada para dobles sin asignación heredada.
    /// </summary>
    Task<string?> ObtenerRolDeAsignacionHeredadaAsync() => Task.FromResult<string?>(null);

    /// <summary>
    /// Alias obsoleto de <see cref="ObtenerRolEfectivoAsync"/> (decisión P7,
    /// 2026-09-23). El nombre no decía qué rol devolvía y dejó que algún
    /// consumidor lo leyera como el rol de origen.
    /// </summary>
    [Obsolete("Usa ObtenerRolEfectivoAsync (autorización, alcance, UI del Tenant actual) u ObtenerRolOrigenAsync (identidad, informes, pertenencia). Decisión P7.")]
    Task<string?> ObtenerRolActualAsync() => ObtenerRolEfectivoAsync();

    /// <summary>
    /// Tenant de origen del usuario (el claim de sesión, ver
    /// Project-Hydra-Negocio/tecnico/docs/MULTITENANCY.md § 8) — a diferencia de <c>ITenantActual.TenantId</c>,
    /// nunca refleja un Delegated Workspace elegido (ADR-004 § 6); es
    /// siempre el tenant al que pertenece la cuenta. Null en las mismas
    /// condiciones que <see cref="ObtenerUsuarioActualIdAsync"/>.
    /// </summary>
    Task<Guid?> ObtenerTenantOrigenIdAsync();

    /// <summary>
    /// Si el usuario actual tiene la autenticación en dos pasos activada
    /// (P1-13 de Project-Hydra-Negocio/MATURITY_REVIEW.md — se exige para abrir un
    /// acceso de soporte, ver AbrirAccesoSoporteCommand). Application no
    /// puede consultar Identity directamente (vive en Infrastructure/Web);
    /// falso también si no hay usuario resuelto — fallo cerrado, igual que
    /// <see cref="ObtenerRolEfectivoAsync"/>.
    /// </summary>
    Task<bool> TieneDobleFactorActivoAsync();
}
