using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;

namespace CaeManager.Application.Inbound.Comercial;

/// <summary>
/// Quién puede dar de baja o corregir la línea Inbound del Producto Contratado: solo una Sesión Privilegiada con la
/// capacidad <see cref="CapacidadPrivilegio.Aprovisionamiento"/> sobre el Tenant activo, que es el Tenant beneficiario,
/// con camino de escritura y sin impersonación (el Actor real no actúa como el Usuario simulado). Nunca el rol efectivo:
/// <c>AutorizacionEscrituraBehavior</c> deja pasar sin Sesión Privilegiada a quien tenga un rol con escritura, y un
/// Administrador del propio Tenant que pudiera tocar su línea Inbound se abriría o se cerraría la lectura entre Tenants
/// (gate DN-7). Es la misma comprobación que repite <c>ContratarLineaInboundCommandHandler</c>.
///
/// <para>
/// <see cref="ISesionPrivilegiadaActual.ObtenerAsync"/> y no <c>RevalidarAsync</c>: el handler corre ya con la conexión
/// elevada a <c>cae_app_aprovisionamiento</c>, que no lee las tablas del plano de Plataforma, y la memo la dejó en fresco
/// <c>ElevacionEscrituraAprovisionamientoBehavior</c> justo antes de elevar. Ventana declarada y aceptada (opción (a) del
/// 2026-11-15, la misma de todo el aprovisionamiento): si entre esa revalidación y el COMMIT se cierra la sesión o se
/// revoca la concesión, la escritura se confirma igual; la base no la cierra porque el contexto firmado no lleva la sesión.
/// </para>
/// </summary>
public static class AutoridadSobreLaLineaInbound
{
    /// <summary>El Tenant beneficiario sobre el que se puede actuar, o <c>null</c> si no hay autoridad.</summary>
    public static async Task<Guid?> TenantAutorizadoAsync(
        ITenantActual tenantActual, ISesionPrivilegiadaActual sesionPrivilegiadaActual, CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } tenantId
            || await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is not { } sesion
            || sesion.Capacidad != CapacidadPrivilegio.Aprovisionamiento
            || !sesion.TieneCaminoDeEscritura
            || sesion.TenantObjetivoId != tenantId
            || sesion.UsuarioSimuladoId is not null)
            return null;

        return tenantId;
    }
}
