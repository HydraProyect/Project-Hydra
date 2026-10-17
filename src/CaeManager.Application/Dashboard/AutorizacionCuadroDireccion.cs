using CaeManager.Application.Common;

namespace CaeManager.Application.Dashboard;

/// <summary>El cuadro contiene información comercial y de equipo, reservada a Dirección CAE y Coordinador CAE.</summary>
public static class AutorizacionCuadroDireccion
{
    public static async Task ExigirLecturaAsync(ICurrentUserService usuario)
    {
        if (await usuario.ObtenerRolEfectivoAsync() is not ("DireccionCae" or "CoordinadorCae"))
            throw new UnauthorizedAccessException("Solo Dirección CAE y Coordinador CAE pueden leer el Cuadro de dirección.");
    }
}
