using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Usuarios;
using CaeManager.Domain.Tenants;

namespace CaeManager.Application.CapacidadesTenant;

/// <summary>
/// Quién tiene una <see cref="CapacidadTenant"/> en el Tenant actual (decisión D-3 del 2026-11-15). Plano de
/// Propiedad: no es <c>CapacidadPrivilegio</c>, que es del plano de Plataforma, ni una Asignación de Operación.
/// </summary>
public interface IAutoridadCapacidadesTenant
{
    /// <summary>
    /// Si quien ejecuta es el Administrador del Tenant propietario operando su propio Tenant: rol efectivo
    /// Administrador, leído además en la base, sin contexto cruzado, sin impersonación y sin Sesión Privilegiada.
    /// Es lo único que concede o revoca una capacidad.
    /// </summary>
    Task<bool> EsAdministradorDelTenantPropietarioAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Si quien ejecuta puede ejercer <paramref name="capacidad"/> en el Tenant actual: el Administrador del
    /// Tenant propietario siempre; cualquier otra cuenta, solo si es del propio Tenant (sin contexto cruzado), está
    /// activa con un rol de escritura y tiene una concesión vigente. Falla cerrado ante cualquier dato que falte.
    /// </summary>
    Task<bool> TieneCapacidadAsync(CapacidadTenant capacidad, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAutoridadCapacidadesTenant"/>
/// <remarks>
/// <para>
/// <b>Por qué el contexto cruzado cierra.</b> Un Gestor CAE de un Operador CAE externo opera el Tenant
/// propietario con Tenant de origen distinto: registra solicitudes, pero ni las resuelve ni puede recibir la
/// capacidad (el beneficiario de una concesión tiene que ser una cuenta de este Tenant, ver
/// <c>ConcederCapacidadTenantCommand</c>). Lo mismo quien administra el Tenant por Encargo de administración:
/// su rol efectivo puede ser Administrador, pero no es el Administrador del Tenant propietario.
/// </para>
/// <para>
/// <b>Por qué se relee la base.</b> El rol efectivo sale de la sesión; la concesión y la pertenencia, de la
/// base en cada decisión, sin seguimiento, así que una revocación o una desactivación se notan en la siguiente
/// petición.
/// </para>
/// </remarks>
public sealed class AutoridadCapacidadesTenant(
    ITenantActual tenantActual,
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiada,
    IActorAuditoria actorAuditoria,
    IAdministradorDelTenantPropietario administradorDelTenant,
    IDirectorioUsuariosService directorioUsuarios,
    IConcesionCapacidadTenantRepository concesiones)
    : IAutoridadCapacidadesTenant
{
    private const string RolAdministrador = "Administrador";

    public async Task<bool> EsAdministradorDelTenantPropietarioAsync(CancellationToken cancellationToken)
    {
        var propio = await ContextoPropioAsync(cancellationToken);
        return propio is { } p
            && string.Equals(p.RolEfectivo, RolAdministrador, StringComparison.Ordinal)
            && await administradorDelTenant.EsAdministradorEnBaseAsync(p.UsuarioId, p.TenantId, cancellationToken);
    }

    public async Task<bool> TieneCapacidadAsync(CapacidadTenant capacidad, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(capacidad))
            return false;

        var propio = await ContextoPropioAsync(cancellationToken);
        if (propio is not { } p)
            return false;

        if (string.Equals(p.RolEfectivo, RolAdministrador, StringComparison.Ordinal)
            && await administradorDelTenant.EsAdministradorEnBaseAsync(p.UsuarioId, p.TenantId, cancellationToken))
            return true;

        return AutorizacionEscrituraBehavior<ICommand, CaeManager.Domain.Common.Result>.RolesConEscritura
                   .Contains(p.RolEfectivo, StringComparer.Ordinal)
            && await directorioUsuarios.EsCuentaActivaConRolAsync(p.UsuarioId, p.TenantId, p.RolEfectivo, cancellationToken)
            && await concesiones.TieneVigenteAsync(p.UsuarioId, capacidad, cancellationToken);
    }

    /// <summary>
    /// El Tenant, la cuenta y el rol efectivo de quien ejecuta, solo si opera su propio Tenant como sí mismo:
    /// sin Sesión Privilegiada, sin impersonación, por la vía normal y sin contexto cruzado. Nulo en cualquier
    /// otro caso.
    /// </summary>
    private async Task<(Guid TenantId, Guid UsuarioId, string RolEfectivo)?> ContextoPropioAsync(
        CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } tenantId
            || await currentUserService.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
            return null;

        if (await sesionPrivilegiada.RevalidarAsync(cancellationToken) is not null)
            return null;

        var actor = await actorAuditoria.ObtenerAsync();
        if (actor.UsuarioSimuladoId is not null
            || actor.ActorRealUsuarioId != usuarioId
            || actor.Via != TipoViaAcceso.Normal)
            return null;

        if (RolesReservadosAlTenantDeOrigen.EsContextoCruzado(
                await currentUserService.ObtenerTenantOrigenIdAsync(), tenantId))
            return null;

        return await currentUserService.ObtenerRolEfectivoAsync() is { } rol
            ? (tenantId, usuarioId, rol)
            : null;
    }
}
