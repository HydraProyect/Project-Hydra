using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;

namespace CaeManager.Application.Tenants.Logo;

/// <summary>
/// Quién puede escribir el logo de un Tenant (contrato del selector de Tenant, § 4.1.3 e invariante
/// I7). Autorizador propio y no <c>AutorizacionEscrituraBehavior</c>: ese behavior deja pasar a
/// cualquier rol de gestión y resuelve el rol efectivo, que en el Tenant de origen sale de la claim sin
/// consultar la base (revisión Codex C6). Aquí solo pasan dos actores:
/// <list type="bullet">
/// <item>el Administrador del Tenant propietario, con Tenant y rol leídos de la base en el momento
/// (mismo predicado que <see cref="IAutorizacionDelegacionTenant"/>): con otro Tenant seleccionado por
/// la vía de Operación, su Tenant de origen no coincide con el objetivo y se deniega;</item>
/// <item>Soporte TALVEG dentro de una Sesión Privilegiada sobre ESE Tenant, con la capacidad
/// <see cref="CapacidadPrivilegio.Aprovisionamiento"/> y sin simulación (decisión 4). La concesión
/// global <c>SoporteLectura</c> no basta.</item>
/// </list>
/// Nunca un Gestor CAE ni un Coordinador CAE: la Operación no concede roles de Propiedad.
/// </summary>
public interface IAutorizacionLogoTenant
{
    Task<bool> PuedeEscribirAsync(Guid tenantObjetivoId, CancellationToken cancellationToken = default);
}

public class AutorizacionLogoTenant(
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    ICurrentUserService currentUserService,
    IAutorizacionDelegacionTenant autorizacionAdministrador)
    : IAutorizacionLogoTenant
{
    public async Task<bool> PuedeEscribirAsync(Guid tenantObjetivoId, CancellationToken cancellationToken = default)
    {
        if (tenantObjetivoId == Guid.Empty) return false;

        // ObtenerAsync y no RevalidarAsync: el pipeline de un comando (AutorizacionEscrituraBehavior y
        // ElevacionEscrituraAprovisionamientoBehavior) acaba de revalidar la sesión contra la base en
        // este mismo ámbito, y el resultado fresco queda como memo. Revalidar aquí consultaría
        // SesionesPrivilegiadas con la conexión ya elevada a cae_app_aprovisionamiento, que no tiene
        // permiso sobre esa tabla.
        if (await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is { } sesion)
            return sesion.Capacidad == CapacidadPrivilegio.Aprovisionamiento
                   && sesion.TieneCaminoDeEscritura
                   && sesion.TenantObjetivoId == tenantObjetivoId
                   && sesion.UsuarioSimuladoId is null;

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return false;

        return await autorizacionAdministrador.PuedeGestionarDelegacionesAsync(
            usuarioId.Value, tenantObjetivoId, cancellationToken);
    }
}

public static class ErroresLogoTenant
{
    public static readonly Domain.Common.Error NoAutorizado = Domain.Common.Error.Crear(
        "LogoTenant.NoAutorizado", "No puedes cambiar el logo de esta organización.");
}
