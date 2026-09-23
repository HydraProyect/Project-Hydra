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
/// tiene su propio camino (no /usuarios). La regla no consulta la marca de
/// plataforma del Tenant a propósito: compara identidades de Tenant, nada más.
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

    public static Result Verificar(string? rol, Guid? tenantOrigenId, Guid? tenantContextoId) =>
        EsReservado(rol) && EsContextoCruzado(tenantOrigenId, tenantContextoId)
            ? Result.Fallo(Error.Crear(
                "Usuarios.RolReservadoAlTenantDeOrigen",
                "Administrador y Dirección CAE solo se asignan desde tu propia organización, " +
                "no desde el espacio de otra a la que accedes por delegación."))
            : Result.Exito();
}
