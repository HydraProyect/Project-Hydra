using CaeManager.Application.Common;

namespace CaeManager.Application.Tenants;

/// <summary>
/// Quién reactiva una Asignación de Operación revocada (legacy <c>DelegacionTenant</c>): solo el
/// Tenant propietario, por medio de su Administrador; el Operador CAE externo nunca (decisión
/// SEG-7 del 2026-10-31: la revocación pertenece al plano de Propiedad y quien la sufre no la
/// deshace desde el plano de Operación). La regla no mira quién revocó: vale igual para lo que
/// revocó el Operador CAE externo y para lo que revocó Soporte TALVEG a petición del Tenant
/// propietario. El mismo predicado decide retirar a una persona de un Operador CAE externo
/// (<c>RevocarAsignacionOperadorDelegadoCommand</c>), otro acto del plano de Propiedad.
///
/// <para>Hacen falta todas estas condiciones:</para>
/// <list type="bullet">
/// <item>Hay usuario y Tenant de origen en la sesión, y ese origen es el Tenant propietario. La
/// autoridad sale de la pertenencia de la cuenta: el Administrador de un Operador CAE externo
/// dentro de un Workspace operativo derivado, o con Encargo de administración, tiene el Tenant
/// activo correcto y ninguna autoridad aquí.</item>
/// <item>El Tenant activo es ese mismo origen: se reactiva desde la casa de uno, nunca desde un
/// Workspace operativo derivado de un tercero. Solo restringe.</item>
/// <item>El rol efectivo de la sesión es Administrador. Falla cerrado en una Sesión Privilegiada
/// de plataforma (Soporte TALVEG no reactiva, tampoco simulando a un Administrador) y en una
/// sesión limitada a Consulta: la pantalla no ofrece lo que <c>AutorizacionEscrituraBehavior</c>
/// denegaría después.</item>
/// <item>La cuenta está activa y es Administrador de ese Tenant, leído en base
/// (<see cref="IAutorizacionDelegacionTenant"/>, el mismo predicado que autoriza por primera vez
/// al Operador CAE externo): la sesión de una cuenta desactivada o degradada no decide.</item>
/// </list>
/// </summary>
internal static class AdministradorQueReactiva
{
    /// <summary>Si la sesión actual actúa como Administrador de ese Tenant propietario, desde su casa.</summary>
    internal static async Task<bool> PuedeAsync(
        Guid tenantPropietarioId,
        ICurrentUserService currentUserService,
        ITenantActual tenantActual,
        IAutorizacionDelegacionTenant autorizacion,
        CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        return usuarioId is not null
               && await currentUserService.ObtenerTenantOrigenIdAsync() == tenantPropietarioId
               && tenantActual.TenantId == tenantPropietarioId
               && await currentUserService.ObtenerRolEfectivoAsync() == EquipoDelOperadorCaeExterno.Administrador
               && await autorizacion.PuedeGestionarDelegacionesAsync(
                   usuarioId.Value, tenantPropietarioId, cancellationToken);
    }
}
