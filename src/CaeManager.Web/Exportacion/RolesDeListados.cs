using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;

namespace CaeManager.Web.Exportacion;

/// <summary>
/// Los roles que entran en las páginas de Vehículos, Proyectos, Visitas y Gestiones (su
/// <c>[Authorize(Roles = …)]</c>): quien exporta el listado es quien lo ve, nadie más. La
/// política por defecto de los endpoints deja pasar a cualquier usuario autenticado, rol
/// Cliente incluido, así que cada exportación la declara. Mismo criterio que
/// <c>/clientes/exportar.xlsx</c>.
/// </summary>
public static class RolesDeListados
{
    public static TBuilder SoloRolesDelListado<TBuilder>(this TBuilder endpoint) where TBuilder : IEndpointConventionBuilder =>
        endpoint.RequireAuthorization(politica => politica.RequireRole(
            Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae, Roles.Consulta));
}
