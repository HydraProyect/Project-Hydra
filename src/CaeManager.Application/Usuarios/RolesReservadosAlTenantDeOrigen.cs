using CaeManager.Domain.Common;

namespace CaeManager.Application.Usuarios;

/// <summary>
/// Regla de alta y cambio de rol de una cuenta desde /usuarios (decisión del
/// propietario, 2026-09-23): <b>Administrador y Dirección CAE solo se conceden
/// cuando el Context Workspace activo es el Tenant de origen de quien actúa.</b>
///
/// <para>
/// Por qué: la cuenta nace con el <c>TenantId</c> del Context Workspace activo
/// (<c>ITenantActual.TenantId</c>), no con el del actor. Desde el Context
/// Workspace de un Tenant propietario al que se llega por Operación (Asignación
/// de Cartera o delegación), dar de alta un Administrador creaba una cuenta del
/// Tenant propietario con autoridad de Propiedad: la Operación convertida en
/// Propiedad (ADR-011 § 1, «tenant isolation ≠ operational delegation»). Los
/// roles de Operación (Coordinador CAE, Gestor CAE, Consulta) y los de portal
/// no son autoridad de Propiedad y no se restringen aquí.
/// </para>
///
/// <para>
/// <b>Sin excepción de plataforma.</b> Un actor cuyo Tenant de origen es el
/// Tenant de plataforma de TALVEG tampoco concede estos roles en el Context
/// Workspace de otro Tenant: Soporte TALVEG no es Operador CAE ni Administrador
/// del Tenant propietario, y el aprovisionamiento de su primer Administrador
/// tiene su propio camino (no /usuarios). Para el Tenant de un Operador CAE
/// externo ese camino es <c>CrearOperadorCaeExternoCommand</c>: el Actor de
/// Plataforma TALVEG indica al primer Administrador en el mismo acto del alta, y
/// la cuenta nace en el Tenant nuevo dentro de la misma transacción (decisión del
/// propietario, 2026-10-08); no hay un Command que lo siembre después. La regla
/// no consulta la marca de plataforma del Tenant a propósito: compara identidades
/// de Tenant, nada más.
/// </para>
///
/// <para>
/// <b>Excepciones: el usuario del Operador CAE externo que opera el Tenant por
/// Asignación de Operación, y el Encargo de administración pleno.</b> Decisión Chris
/// 2026-10-20: el perfil de origen rige en todos los Tenants beneficiarios delegados
/// con todos sus poderes, también los de Propiedad, sin encargo; deroga la regla del
/// 2026-09-23 («la Operación nunca concede roles de Propiedad»). Quien opera así el
/// Tenant con un rol que gestiona cuentas puede conceder estos roles en él. El encargo
/// pleno (decisión del 2026-10-15) se conserva como segunda vía, aunque ya no eleva a
/// nadie (deuda). Soporte TALVEG no entra por ninguna de las dos: su Sesión
/// Privilegiada no tiene rol de negocio.
/// </para>
///
/// <para>
/// Falla cerrado: sin Tenant de origen o sin Context Workspace resueltos, el
/// contexto se trata como cruzado. Los literales de rol se repiten a propósito
/// (Application no referencia <c>Roles</c> de Infrastructure.Identity, mismo
/// criterio que <c>AutorizacionEscrituraBehavior</c>).
/// </para>
/// </summary>
public static class RolesReservadosAlTenantDeOrigen
{
    public static readonly IReadOnlyList<string> Roles = ["Administrador", "DireccionCae"];

    public static bool EsReservado(string? rol) => rol is not null && Roles.Contains(rol);

    /// <summary>
    /// Cruzado = el Context Workspace activo no es el Tenant de origen del
    /// actor, o alguno de los dos no se pudo resolver.
    /// </summary>
    public static bool EsContextoCruzado(Guid? tenantOrigenId, Guid? tenantContextoId) =>
        tenantOrigenId is null || tenantContextoId is null || tenantOrigenId != tenantContextoId;

    /// <summary>
    /// Quien actúa tiene autoridad de Propiedad en el Tenant activo aunque no sea su
    /// Tenant de origen: lo opera por Asignación de Operación con su perfil de origen
    /// (decisión Chris 2026-10-20) o por un Encargo de administración pleno. Sin la señal
    /// (<c>null</c>) falla cerrado.
    /// </summary>
    public static async Task<bool> ActuaConAutoridadDePropiedadAsync(Common.IEncargoDeAdministracionActual? encargoActual) =>
        encargoActual is not null
        && (await encargoActual.OperaPorAsignacionDeOperacionAsync()
            || await encargoActual.EncargoPlenoAsync() is not null);

    public static Result Verificar(string? rol, Guid? tenantOrigenId, Guid? tenantContextoId, bool encargoPleno) =>
        EsReservado(rol) && EsContextoCruzado(tenantOrigenId, tenantContextoId) && !encargoPleno
            ? Result.Fallo(Error.Crear(
                "Usuarios.RolReservadoAlTenantDeOrigen",
                "Administrador y Dirección CAE solo se asignan desde tu propia organización, " +
                "no desde el espacio de otra a la que accedes por delegación."))
            : Result.Exito();
}
