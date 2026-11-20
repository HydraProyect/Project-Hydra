using CaeManager.Application.Common;
using CaeManager.Domain.Comercial;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comercial.LineaOutbound;

/// <summary>
/// ¿Es «solo Inbound» el Producto Contratado de un Tenant beneficiario? Decisión de Chris del 2026-11-19, opción A'
/// (DECISION_LOG e496f33): lo es cuando se cumplen las tres condiciones a la vez: tiene la línea
/// <see cref="LineaProductoContratado.Inbound"/> vigente, esa fila lleva marcada la declaración
/// <see cref="ProductoContratado.DeclaraSoloInbound"/> y no tiene ninguna línea
/// <see cref="LineaProductoContratado.OutboundGestionPropia"/> en ningún estado (vigente, también con el inicio aún no alcanzado, o finalizada). «En
/// ningún estado» no incluye las filas eliminadas (borrado lógico, <c>EstaEliminado</c>): una fila eliminada no cuenta, ni
/// de la línea Outbound ni de la Inbound. Es el plano comercial: la autoridad la ejerce
/// <see cref="ICriterioDeOutbound"/>, que con el papel de Tenant de origen cierra Outbound a ese Tenant.
///
/// <para>
/// <b>La declaración es lo que distingue al Tenant histórico.</b> No hay backfill (<see cref="IEstadoLineaOutbound"/>): los
/// Tenants que ya operan Outbound no tienen fila de la línea Outbound, así que la ausencia de esa fila no basta para
/// cerrarles Outbound al contratar Inbound. Sin la declaración (casilla opcional, <c>false</c> por defecto y en todas las
/// filas anteriores a la columna) el Tenant conserva Outbound aunque tenga la línea Inbound vigente y ninguna Outbound. Un
/// Tenant sin ninguna línea tampoco es solo Inbound.
/// </para>
///
/// <para>
/// La línea Inbound cuenta con el criterio de <c>LineaInboundVigenteQuery</c> y del gate de la base: estado
/// <see cref="EstadoProductoContratado.Vigente"/>, no eliminada y con el inicio ya alcanzado; la declaración se lee en esa
/// misma fila, nunca en una finalizada. La línea Outbound cuenta en cualquier estado (vigente o finalizada) y
/// con cualquier inicio mientras no esté eliminada: «ninguna línea Outbound» es que no hay ninguna fila no eliminada de esa
/// línea, y una finalizada o una vigente con el inicio aún no alcanzado (solo posible con un desfase de reloj entre quien
/// la escribe y quien la lee, o con una fila escrita fuera de <c>ProductoContratado.Contratar</c>) sí es una línea. Así, ante la
/// duda, el Tenant conserva Outbound: lo único que esta lectura puede hacer es cerrarlo, y solo lo cierra cuando el
/// Producto Contratado dice exactamente «solo Inbound».
/// </para>
///
/// <para>
/// Se lee bajo la identidad de runtime con la política <c>aislamiento_tenant</c> de <c>ProductosContratados</c> y su
/// SELECT de tabla entera, que incluye la columna de la declaración: solo ve las filas del Tenant activo, así que
/// preguntar por otro Tenant responde «no» (no hay línea Inbound visible) y no cierra nada. No se consulta bajo <c>cae_app_aprovisionamiento</c>, cuya política RESTRICTIVE oculta las filas que no son
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
        // Las filas eliminadas (borrado lógico) se descartan aquí, para las dos líneas: no cuentan en ningún estado.
        var filas = await contexto.ProductosContratados
            .Where(p => p.TenantId == tenantId
                && !p.EstaEliminado
                && (p.Linea == LineaProductoContratado.Inbound || p.Linea == LineaProductoContratado.OutboundGestionPropia))
            .Select(p => new { p.Linea, p.Estado, p.VigenteDesdeUtc, p.DeclaraSoloInbound })
            .ToListAsync(cancellationToken);

        // Las tres condiciones de la opción A' (DECISION_LOG e496f33). La declaración cuenta en la misma fila de la línea
        // Inbound vigente: la de una fila finalizada o programada no vale.
        var inboundVigenteDeclarada = filas.Any(f => f.Linea == LineaProductoContratado.Inbound
            && f.Estado == EstadoProductoContratado.Vigente
            && f.VigenteDesdeUtc <= ahora
            && f.DeclaraSoloInbound);
        // La línea Outbound cuenta en cualquier estado (vigente o finalizada) y con cualquier inicio, aunque no haya llegado.
        var algunaOutbound = filas.Any(f => f.Linea == LineaProductoContratado.OutboundGestionPropia);

        return inboundVigenteDeclarada && !algunaOutbound;
    }
}
