using CaeManager.Application.Common;

namespace CaeManager.Application.Tenants;

/// <summary>
/// Quién revoca y reactiva el acceso de un Operador CAE externo a un Tenant propietario: la
/// Asignación de Operación delegada entera (legacy <c>DelegacionTenant</c> de propósito
/// <c>OperadorExterno</c>). Decisión de producto del 2026-10-26: «Solo el Gestor CAE del
/// Operador CAE externo revoca y reactiva la Delegación». El Administrador del Tenant
/// propietario no revoca ni reactiva; tampoco el Administrador, la Dirección CAE ni el
/// Coordinador CAE del Operador CAE externo, que no son Gestor CAE.
///
/// <para>
/// Una sola regla para los dos comandos y para las consultas con las que la pantalla decide
/// si pinta «Revocar acceso» y «Reactivar». Cuatro condiciones, y hacen falta las cuatro:
/// </para>
/// <list type="bullet">
/// <item>Hay usuario y Tenant de origen en la sesión (fallo cerrado fuera de un circuito
/// autenticado).</item>
/// <item>El Tenant de origen es el del Operador CAE externo. La autoridad sale de la
/// pertenencia de la cuenta, nunca del Tenant activo: dentro de un Workspace operativo
/// derivado el Tenant activo es el propietario y no concede nada.</item>
/// <item>La sesión tiene rol de negocio. Falla cerrado en una Sesión Privilegiada de
/// plataforma: Soporte TALVEG no es Gestor CAE ni Operador CAE.</item>
/// <item>La cuenta está activa y tiene el rol de Identity <c>GestorCae</c> en ese Tenant de
/// origen, leído en base y no en el claim: dentro de un Workspace operativo derivado el claim
/// es el de la cartera, y la sesión de una cuenta desactivada no debe seguir decidiendo.</item>
/// </list>
///
/// <para>
/// No exige que el Gestor CAE tenga Asignación de Cartera sobre ese Tenant propietario: la
/// decisión no lo pide, y tras revocar las carteras están cerradas, así que exigirlo haría
/// imposible reactivar.
/// </para>
/// </summary>
public static class GestorCaeDelOperadorCaeExterno
{
    private const string GestorCae = "GestorCae";

    public static async Task<bool> EsAsync(
        Guid operadorTenantId,
        ICurrentUserService currentUserService,
        IDirectorioUsuariosService directorioUsuarios,
        CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var origen = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (usuarioId is null || origen is null || origen.Value != operadorTenantId)
            return false;

        // Las cuentas del Operador CAE externo se leen desde su propia casa, mismo criterio
        // que AutoridadSobreCarteraDeGestorCae.ResolverActorAsync.
        using (AmbitoTenantExplicito.Establecer(origen.Value))
        {
            if (await currentUserService.ObtenerRolEfectivoAsync() is null)
                return false;

            return await directorioUsuarios.EsCuentaActivaConRolAsync(
                usuarioId.Value, origen.Value, GestorCae, cancellationToken);
        }
    }
}
