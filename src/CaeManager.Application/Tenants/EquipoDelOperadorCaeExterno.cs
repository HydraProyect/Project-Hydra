using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;

namespace CaeManager.Application.Tenants;

/// <summary>
/// Quién revoca y reactiva el acceso de un Operador CAE externo a un Tenant propietario: la
/// Asignación de Operación delegada entera (legacy <c>DelegacionTenant</c> de propósito
/// <c>OperadorExterno</c>). Decisiones de producto del 2026-10-26: revoca y reactiva el equipo
/// del propio Operador CAE externo —Gestor CAE, Coordinador CAE, Dirección CAE y
/// Administrador—, siempre que tenga alcance sobre ese Tenant propietario. No pueden el
/// Administrador del Tenant propietario ni nadie de otro Operador CAE. La salida a petición del
/// Tenant propietario la hace Soporte TALVEG con Sesión Privilegiada, no este camino.
///
/// <para>
/// Una sola regla para los dos comandos y para las consultas con las que la pantalla decide
/// si pinta «Revocar acceso» y «Reactivar». Hacen falta todas estas condiciones:
/// </para>
/// <list type="bullet">
/// <item>Hay usuario y Tenant de origen en la sesión (fallo cerrado fuera de un circuito
/// autenticado).</item>
/// <item>El Tenant de origen es el del Operador CAE externo. La autoridad sale de la
/// pertenencia de la cuenta, nunca del Tenant activo: dentro de un Workspace operativo
/// derivado el Tenant activo es el propietario y no concede nada.</item>
/// <item>El rol de la sesión en origen (<see cref="ICurrentUserService.ObtenerRolEfectivoAsync"/>
/// con el ámbito del Tenant de origen) es uno de los cuatro del equipo. Falla cerrado en una
/// Sesión Privilegiada de plataforma (Soporte TALVEG no es Gestor CAE ni Operador CAE), y
/// también con una sesión limitada a Consulta por login local: la pantalla no ofrece lo que
/// <c>AutorizacionEscrituraBehavior</c> denegaría después.</item>
/// <item>La cuenta está activa y tiene ese mismo rol de Identity en su Tenant de origen, leído
/// en base: la sesión de una cuenta desactivada o degradada no debe seguir decidiendo.</item>
/// <item>Alcance sobre ese Tenant propietario: el Administrador y la Dirección CAE alcanzan toda
/// la organización (mismo criterio que <c>Roles.AlcanzaTodaLaOrganizacion</c>, sin Consulta,
/// que no escribe), y toda la organización llega a los Tenants propietarios con los que el
/// Operador CAE externo tiene esta Asignación de Operación; el Gestor CAE y el Coordinador CAE
/// necesitan una Asignación de Cartera viva propia sobre ese Tenant propietario, bajo una
/// Asignación de Operación de este Operador CAE externo.</item>
/// </list>
///
/// <para>
/// Consecuencia aceptada (decisión del 2026-10-26): revocar cierra las carteras en cascada, así
/// que tras revocar ningún Gestor CAE ni Coordinador CAE puede reactivar; lo hacen el
/// Administrador o la Dirección CAE del Operador CAE externo, o Soporte TALVEG.
/// </para>
/// </summary>
public static class EquipoDelOperadorCaeExterno
{
    // Application no puede referenciar Infrastructure.Identity.Roles: mismos códigos que allí
    // (los nombres reales de AspNetRoles). La paridad la fija RevocacionDelegacionTests.
    internal const string Administrador = "Administrador";
    internal const string DireccionCae = "DireccionCae";
    internal const string CoordinadorCae = "CoordinadorCae";
    internal const string GestorCae = "GestorCae";

    /// <summary>Si el usuario actual puede revocar o reactivar ese acceso.</summary>
    public static async Task<bool> PuedeAsync(
        Guid operadorTenantId,
        Guid tenantPropietarioId,
        ICurrentUserService currentUserService,
        IDirectorioUsuariosService directorioUsuarios,
        ICatalogoIncorporacionCartera catalogo,
        CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var origen = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (usuarioId is null || origen is null || origen.Value != operadorTenantId)
            return false;

        // Las cuentas y las carteras del Operador CAE externo se leen desde su propia casa,
        // mismo criterio que ContextoOperadorCae y AutoridadSobreCarteraDeGestorCae: la
        // política RLS de las carteras solo deja leer las del Operador CAE de la sesión.
        using (AmbitoTenantExplicito.Establecer(origen.Value))
        {
            var rol = await currentUserService.ObtenerRolEfectivoAsync();
            if (rol is not (Administrador or DireccionCae or CoordinadorCae or GestorCae)
                || !await directorioUsuarios.EsCuentaActivaConRolAsync(
                    usuarioId.Value, origen.Value, rol, cancellationToken))
                return false;

            if (rol is Administrador or DireccionCae)
                return true;

            var carteras = await catalogo.ObtenerCarterasVivasAsync(
                operadorTenantId, tenantPropietarioId, cancellationToken);
            return carteras.Any(c => c.UsuarioId == usuarioId.Value);
        }
    }
}
