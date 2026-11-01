using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma.Commands.CerrarSesionPrivilegiada;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using MediatR;

namespace CaeManager.Web.Components.Account;

/// <summary>
/// Lo que cerrar sesión hace en el servidor, además de borrar la cookie del navegador.
///
/// <para>
/// <b>Esta sesión.</b> <see cref="CerrarEstaSesionAsync"/> revoca solo el identificador de sesión
/// de la cookie que llega (<see cref="SignInManagerCuentaDesactivada.ClaimSid"/>): una copia de esa
/// cookie deja de valer en la revalidación siguiente (60 s), y las demás sesiones de la misma
/// cuenta (otro navegador, otro dispositivo) siguen vivas. Un fallo aquí se registra y no impide
/// cerrar la sesión en el navegador.
/// </para>
///
/// <para>
/// <b>Todos los dispositivos.</b> <see cref="CerrarTodasLasSesionesAsync"/> rota el sello de seguridad
/// de la cuenta y revoca todas sus sesiones (<see cref="IRegistroSesionesUsuario.CerrarTodasAsync"/>).
/// Un fallo se propaga: decir «cerradas» sin haberlas cerrado sería peor que un error.
/// </para>
///
/// <para>
/// <b>Sesión Privilegiada.</b> Si la petición tiene seleccionada una Sesión Privilegiada (cookie
/// <c>cae_cliente_activo</c>), se cierra con <see cref="CerrarSesionPrivilegiadaCommand"/> en nombre
/// del Actor real, que es quien firma la cookie: el comando solo cierra la que esa persona abrió.
/// El Usuario simulado no se toca: ni sus sesiones ni su sello. Todo va en un ámbito de servicios
/// nuevo con la selección descartada (<see cref="ClienteActivoSeleccionado.Invalidar"/>): con ella,
/// la conexión adopta el rol de solo lectura de soporte y ninguna de las dos escrituras pasaría
/// (el mismo motivo por el que <c>SesionSoporteEndpoints.CerrarAsync</c> exige salir antes).
/// </para>
/// </summary>
public static class CierreSesionEnServidor
{
    public static async Task CerrarEstaSesionAsync(HttpContext httpContext, ILogger logger)
    {
        if (LeerCuenta(httpContext.User) is not { } usuarioId)
            return;
        var sid = SignInManagerCuentaDesactivada.LeerSid(httpContext.User);
        var sesionPrivilegiadaId = SesionPrivilegiadaSeleccionada(httpContext);

        await using var ambito = AmbitoSinSeleccion(httpContext);
        await CerrarSesionPrivilegiadaAsync(ambito.ServiceProvider, sesionPrivilegiadaId, logger);

        if (sid is not { } esta)
            return;
        try
        {
            await ambito.ServiceProvider.GetRequiredService<IRegistroSesionesUsuario>().RevocarAsync(usuarioId, esta);
        }
        catch (Exception ex)
        {
            logger.LogError("No se pudo revocar en el servidor la sesión que se cierra ({TipoError}); la cookie se borra igual.",
                ex.GetType().Name);
        }
    }

    public static async Task CerrarTodasLasSesionesAsync(HttpContext httpContext, ILogger logger)
    {
        if (LeerCuenta(httpContext.User) is not { } usuarioId)
            return;
        var sesionPrivilegiadaId = SesionPrivilegiadaSeleccionada(httpContext);

        await using var ambito = AmbitoSinSeleccion(httpContext);
        await CerrarSesionPrivilegiadaAsync(ambito.ServiceProvider, sesionPrivilegiadaId, logger);
        await ambito.ServiceProvider.GetRequiredService<IRegistroSesionesUsuario>().CerrarTodasAsync(usuarioId);
    }

    private static Guid? LeerCuenta(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id != Guid.Empty ? id : null;

    private static Guid? SesionPrivilegiadaSeleccionada(HttpContext httpContext) =>
        httpContext.RequestServices.GetService<IClienteActivoSeleccionado>()?.SesionPrivilegiadaIdSeleccionada;

    private static AsyncServiceScope AmbitoSinSeleccion(HttpContext httpContext)
    {
        var ambito = httpContext.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        (ambito.ServiceProvider.GetService<IClienteActivoSeleccionado>() as ClienteActivoSeleccionado)?.Invalidar();
        return ambito;
    }

    private static async Task CerrarSesionPrivilegiadaAsync(IServiceProvider servicios, Guid? sesionPrivilegiadaId, ILogger logger)
    {
        if (sesionPrivilegiadaId is not { } id)
            return;
        try
        {
            var resultado = await servicios.GetRequiredService<ISender>().Send(new CerrarSesionPrivilegiadaCommand(id));
            if (resultado.EsFallido)
                logger.LogWarning("Al cerrar sesión no se cerró la Sesión Privilegiada {SesionPrivilegiadaId}: {Codigo}.",
                    id, resultado.Error.Codigo);
        }
        catch (Exception ex)
        {
            logger.LogError("Al cerrar sesión falló el cierre de la Sesión Privilegiada {SesionPrivilegiadaId} ({TipoError}).",
                id, ex.GetType().Name);
        }
    }
}
