using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;

namespace CaeManager.Application.Tenants;

/// <summary>
/// Quién revoca el acceso de un Operador CAE externo a un Tenant propietario: la Asignación de
/// Operación delegada entera (legacy <c>DelegacionTenant</c> de propósito
/// <c>OperadorExterno</c>). Decisiones de producto del 2026-10-26: revoca el equipo del propio
/// Operador CAE externo —Gestor CAE, Coordinador CAE, Dirección CAE y Administrador—, siempre
/// que tenga alcance sobre ese Tenant propietario. No pueden el Administrador del Tenant
/// propietario ni nadie de otro Operador CAE. La salida a petición del Tenant propietario la
/// hace Soporte TALVEG con Sesión Privilegiada, no este camino. Reactivar no es de este equipo:
/// es solo del Tenant propietario (<see cref="AdministradorQueReactiva"/>, decisión SEG-7).
///
/// <para>
/// Una sola regla para el comando y para las consultas con las que la pantalla decide si pinta
/// «Revocar acceso». Hacen falta todas estas condiciones:
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
/// </summary>
public static class EquipoDelOperadorCaeExterno
{
    // Application no puede referenciar Infrastructure.Identity.Roles: mismos códigos que allí
    // (los nombres reales de AspNetRoles). La paridad la fija RevocacionDelegacionTests.
    internal const string Administrador = "Administrador";
    internal const string DireccionCae = "DireccionCae";
    internal const string CoordinadorCae = "CoordinadorCae";
    internal const string GestorCae = "GestorCae";

    /// <summary>Si el usuario actual puede revocar ese acceso.</summary>
    public static async Task<bool> PuedeAsync(
        Guid operadorTenantId,
        Guid tenantPropietarioId,
        ICurrentUserService currentUserService,
        IDirectorioUsuariosService directorioUsuarios,
        ICatalogoIncorporacionCartera catalogo,
        CancellationToken cancellationToken)
    {
        var autorizados = await PuedeEnLoteAsync(
            [(operadorTenantId, tenantPropietarioId)], currentUserService, directorioUsuarios, catalogo, cancellationToken);
        return autorizados.Contains((operadorTenantId, tenantPropietarioId));
    }

    internal static async Task<HashSet<(Guid OperadorTenantId, Guid TenantPropietarioId)>> PuedeEnLoteAsync(
        IReadOnlyCollection<(Guid OperadorTenantId, Guid TenantPropietarioId)> destinos,
        ICurrentUserService currentUserService, IDirectorioUsuariosService directorioUsuarios,
        ICatalogoIncorporacionCartera catalogo, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var origen = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (usuarioId is null || origen is null)
            return [];

        var propietarios = destinos.Where(d => d.OperadorTenantId == origen.Value)
            .Select(d => d.TenantPropietarioId).ToHashSet();
        if (propietarios.Count == 0)
            return [];

        // La autoridad se comprueba en la casa del Operador CAE externo, nunca en el Tenant activo.
        using (AmbitoTenantExplicito.Establecer(origen.Value))
        {
            var rol = await currentUserService.ObtenerRolEfectivoAsync();
            if (rol is not (Administrador or DireccionCae or CoordinadorCae or GestorCae)
                || !await directorioUsuarios.EsCuentaActivaConRolAsync(
                    usuarioId.Value, origen.Value, rol, cancellationToken))
                return [];

            if (rol is Administrador or DireccionCae)
                return propietarios.Select(p => (origen.Value, p)).ToHashSet();

            var carteras = await catalogo.ObtenerCarterasVivasAsync(
                origen.Value, propietarios.Count == 1 ? propietarios.Single() : null, cancellationToken);
            propietarios.IntersectWith(carteras.Where(c => c.UsuarioId == usuarioId.Value)
                .Select(c => c.PropietarioTenantId));
            return propietarios.Select(p => (origen.Value, p)).ToHashSet();
        }
    }
}
