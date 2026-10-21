using CaeManager.Application.Common;

namespace CaeManager.Application.Dashboard;

/// <summary>
/// El cuadro contiene información comercial y de equipo. Lo leen Dirección CAE, Coordinador CAE y el Administrador del
/// Tenant (decisión de Chris del 2026-10-19), y el administrador de TALVEG solo como Actor de Plataforma dentro de una
/// Sesión Privilegiada de acceso total al Tenant que abrió (<see cref="IPoliticaNotaInterna.TieneAccesoTotalAlTenantAsync"/>,
/// la misma puerta de las fichas 360): esa sesión no trae rol de negocio, queda auditada y no concede escribir.
/// Reabrir un mes sigue siendo solo de Dirección CAE (<c>ReabrirMesCuadroDireccionCommand</c>).
/// </summary>
public static class AutorizacionCuadroDireccion
{
    // Literales, no Infrastructure.Identity.Roles: Application no puede referenciar Infrastructure.
    public static readonly IReadOnlyList<string> RolesQueLeen = ["DireccionCae", "CoordinadorCae", "Administrador"];

    /// <summary>
    /// Si quien mira entra por rol de negocio (no por la Sesión Privilegiada). Lo que es del Operador CAE de quien mira
    /// —sus Tenants beneficiarios autorizados, sus Empresas gestionadas y su Facturación— solo existe para quien entra
    /// por rol: el Actor de Plataforma TALVEG no es Operador CAE, y su Tenant de origen no es el que abrió.
    /// </summary>
    public static async Task<bool> EntraPorRolAsync(ICurrentUserService usuario) =>
        await usuario.ObtenerRolEfectivoAsync() is { } rol && RolesQueLeen.Contains(rol);

    public static async Task ExigirLecturaAsync(ICurrentUserService usuario, IPoliticaNotaInterna politicaAcceso)
    {
        if (await EntraPorRolAsync(usuario))
            return;

        if (await politicaAcceso.TieneAccesoTotalAlTenantAsync())
            return;

        throw new UnauthorizedAccessException("Solo Dirección CAE, Coordinador CAE, el Administrador del Tenant o una Sesión Privilegiada de acceso total pueden leer el Cuadro de dirección.");
    }
}