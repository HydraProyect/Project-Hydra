using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;

namespace CaeManager.Web.Services.Autorizacion;

/// <summary>
/// Nombres de las políticas de las fichas 360 que muestran la «Nota interna», y sus roles de equipo.
/// </summary>
public static class PoliticasFichas360
{
    public const string TipoDocumento = "FichaTipoDocumento360";
    public const string Vehiculo = "FichaVehiculo360";
    public const string Proyecto = "FichaProyecto360";
    public const string Visita = "FichaVisita360";

    /// <summary>Roles de equipo de la ficha Tipo de documento 360 (igual que <c>ObtenerEstadoTipoDocumentoQueryHandler.RolesQueVenLaPagina</c>).</summary>
    public static readonly string[] RolesTipoDocumento =
        [Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae];

    /// <summary>Roles de equipo de la ficha Vehículo 360; incluye Consulta, que la lee sin editar.</summary>
    public static readonly string[] RolesVehiculo =
        [Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae, Roles.Consulta];

    /// <summary>Roles de equipo de Proyecto 360 y Visita 360 (las páginas las leen con Consulta, sin editar).</summary>
    public static readonly string[] RolesProyectoYVisita = RolesVehiculo;
}

/// <summary>
/// Política de página del Cuadro de dirección: Dirección CAE, Coordinador CAE y Administrador del Tenant por rol, o el
/// administrador de TALVEG con Sesión Privilegiada de acceso total (decisión de Chris del 2026-10-19). Las cifras las vuelve
/// a exigir cada query en <c>AutorizacionCuadroDireccion</c>; reabrir sigue siendo solo de Dirección CAE.
/// </summary>
public static class PoliticaCuadroDireccion
{
    public const string Nombre = "CuadroDireccion";

    public static readonly string[] Roles = [Infrastructure.Identity.Roles.DireccionCae, Infrastructure.Identity.Roles.CoordinadorCae, Infrastructure.Identity.Roles.Administrador];
}

/// <summary>
/// Puerta de página de una ficha 360 con «Nota interna». Entra quien tiene uno de los roles de equipo, o quien abre el
/// Tenant con una Sesión Privilegiada de acceso total (<see cref="IPoliticaNotaInterna.TieneAccesoTotalAlTenantAsync"/>).
/// Sustituye a <c>[Authorize(Roles = …)]</c>, que no puede admitir la sesión: el middleware
/// <c>SesionPrivilegiadaSinRolDeNegocioMiddleware</c> quita los claims de rol de esa petición.
///
/// <para>
/// Fallo cerrado: sin rol de equipo y sin sesión de acceso total, no entra nadie. La sesión solo concede la página para
/// leer; la escritura sigue fuera (<c>SoloConEscritura</c> depende de roles y <c>AutorizacionEscrituraBehavior</c> de la
/// capacidad de la sesión).
/// </para>
/// </summary>
public sealed record RequisitoEquipoOSoporteConAccesoTotal(string[] RolesDeEquipo) : IAuthorizationRequirement;

public sealed class RequisitoEquipoOSoporteConAccesoTotalHandler(IPoliticaNotaInterna politicaNotaInterna)
    : AuthorizationHandler<RequisitoEquipoOSoporteConAccesoTotal>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, RequisitoEquipoOSoporteConAccesoTotal requisito)
    {
        if (requisito.RolesDeEquipo.Any(context.User.IsInRole))
        {
            context.Succeed(requisito);
            return;
        }

        if (await politicaNotaInterna.TieneAccesoTotalAlTenantAsync())
            context.Succeed(requisito);
    }
}
