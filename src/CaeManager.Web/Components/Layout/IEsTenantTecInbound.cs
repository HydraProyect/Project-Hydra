namespace CaeManager.Web.Components.Layout;

/// <summary>
/// ¿Es el Tenant activo un Tenant TEC participante de Inbound, es decir, ha concedido como Tenant propietario al menos una
/// autorización de lectura documental que sigue concedida (vigente y sin revocar)? Es el gate del grupo «Mis Clientes»
/// del menú lateral (<c>ContextoMenuLateral.EsTenantTecInbound</c>), independiente del de la línea Inbound
/// (<see cref="ILineaInboundVigente"/>): el Tenant TEC no tiene Producto Contratado de Inbound y no necesita tenerlo.
///
/// <para>
/// Puerto del Núcleo, como <see cref="ILineaInboundVigente"/>: el menú lateral no conoce a Inbound (ADR-013 § 4.1, regla 3), así
/// que la interfaz vive aquí y Inbound registra su implementación
/// (<c>Features.Inbound.EsTenantTecPorAutorizacionConcedida</c>, que despacha <c>TieneAutorizacionLecturaConcedidaQuery</c>).
/// Es presentación, no autoridad: las páginas del grupo se autorizan por su cuenta. Falla cerrado: ante un error, no.
/// </para>
/// </summary>
public interface IEsTenantTecInbound
{
    Task<bool> EsAsync(CancellationToken cancellationToken = default);
}
