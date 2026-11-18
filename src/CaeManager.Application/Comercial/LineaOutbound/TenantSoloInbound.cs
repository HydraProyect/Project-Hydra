using CaeManager.Application.Common;
using CaeManager.Domain.Comercial;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comercial.LineaOutbound;

/// <summary>
/// ¿Es «solo Inbound» el Producto Contratado de un Tenant beneficiario? Decisión de Chris del 2026-11-18 («Inbound sin
/// Outbound»): lo es si tiene la línea <see cref="LineaProductoContratado.Inbound"/> vigente y ninguna línea
/// <see cref="LineaProductoContratado.OutboundGestionPropia"/>. Es el plano comercial: la autoridad la ejerce
/// <see cref="ICriterioDeOutbound"/>, que con el papel de Tenant de origen cierra Outbound a ese Tenant.
///
/// <para>
/// <b>No se exige la línea Outbound a secas.</b> No hay backfill (<see cref="IEstadoLineaOutbound"/>): los Tenants que ya
/// operan Outbound no tienen fila de la línea, y exigirla les quitaría Outbound. Por eso un Tenant sin ninguna línea, ni
/// Inbound ni Outbound, NO es solo Inbound y conserva Outbound como siempre.
/// </para>
///
/// <para>
/// La línea Inbound cuenta con el criterio de <c>LineaInboundVigenteQuery</c> y del gate de la base: estado
/// <see cref="EstadoProductoContratado.Vigente"/>, no eliminada y con el inicio ya alcanzado. La línea Outbound cuenta en
/// cualquier estado (vigente, programada o finalizada) mientras no esté eliminada: «ninguna línea Outbound» es que no hay
/// fila, y una finalizada sí es una línea. Así, ante la duda, el Tenant conserva Outbound: lo único que esta lectura puede
/// hacer es cerrarlo, y solo lo cierra cuando el Producto Contratado dice exactamente «solo Inbound».
/// </para>
///
/// <para>
/// Se lee bajo la identidad de runtime con la política <c>aislamiento_tenant</c> de <c>ProductosContratados</c>: solo ve
/// las filas del Tenant activo, así que preguntar por otro Tenant responde «no» (no hay línea Inbound visible) y no cierra
/// nada. No se consulta bajo <c>cae_app_aprovisionamiento</c>, cuya política RESTRICTIVE oculta las filas que no son
/// Inbound y haría pasar por solo Inbound a un Tenant con las dos líneas. Un error de lectura no se traga: se propaga, y
/// la petición falla entera en vez de abrir o cerrar Outbound en silencio.
/// </para>
///
/// <para>
/// Pasa por la <see cref="PuertaAccesoDatos"/> del scope cuando la hay, porque comparte el <c>DbContext</c> del circuito:
/// la leen la guarda de comandos y el alcance dentro de un despacho de MediatR (donde la puerta es reentrante) y el handler
/// de autorización de los endpoints mínimos de Outbound, fuera de él.
/// </para>
/// </summary>
public interface ITenantSoloInbound
{
    Task<bool> EsSoloInboundAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

public sealed class TenantSoloInboundService(
    ILineaOutboundQueryContext contexto, TimeProvider reloj, PuertaAccesoDatos? puerta = null) : ITenantSoloInbound
{
    public Task<bool> EsSoloInboundAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        puerta is null
            ? LeerAsync(tenantId, cancellationToken)
            : puerta.EjecutarAsync(() => LeerAsync(tenantId, cancellationToken), cancellationToken);

    private async Task<bool> LeerAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime;
        var filas = await contexto.ProductosContratados
            .Where(p => p.TenantId == tenantId
                && !p.EstaEliminado
                && (p.Linea == LineaProductoContratado.Inbound || p.Linea == LineaProductoContratado.OutboundGestionPropia))
            .Select(p => new { p.Linea, p.Estado, p.VigenteDesdeUtc })
            .ToListAsync(cancellationToken);

        var inboundVigente = filas.Any(f => f.Linea == LineaProductoContratado.Inbound
            && f.Estado == EstadoProductoContratado.Vigente
            && f.VigenteDesdeUtc <= ahora);
        var algunaOutbound = filas.Any(f => f.Linea == LineaProductoContratado.OutboundGestionPropia);

        return inboundVigente && !algunaOutbound;
    }
}
