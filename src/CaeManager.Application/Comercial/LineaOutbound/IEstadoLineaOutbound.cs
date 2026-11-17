using CaeManager.Domain.Comercial;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comercial.LineaOutbound;

/// <summary>
/// Estado de la línea <see cref="LineaProductoContratado.OutboundGestionPropia"/> del Producto Contratado de un Tenant
/// beneficiario, tal como lo lee <see cref="IEstadoLineaOutbound"/>.
/// </summary>
public enum EstadoLineaOutbound
{
    /// <summary>Ningún Producto Contratado de la línea Outbound, no eliminado, del Tenant beneficiario.</summary>
    SinLinea,

    /// <summary>
    /// Un Producto Contratado de la línea Outbound <see cref="EstadoProductoContratado.Vigente"/> cuyo inicio todavía no
    /// ha llegado, y ninguno ya en vigor. Inalcanzable mientras no se decida el inicio diferido
    /// (<c>ProductoContratado.Contratar</c>); existe para que una fila así nunca caiga en <see cref="Finalizada"/>.
    /// La única salvedad es el reloj: el inicio se sella con el <c>now()</c> de la base y aquí se compara con el de la
    /// aplicación, así que un desfase puede dar Programada unos segundos justo después de contratar (falla cerrado).
    /// </summary>
    Programada,

    /// <summary>
    /// Un Producto Contratado de la línea Outbound <see cref="EstadoProductoContratado.Vigente"/>, no eliminado y con el
    /// inicio ya alcanzado.
    /// </summary>
    Vigente,

    /// <summary>
    /// Solo Productos Contratados de la línea Outbound <see cref="EstadoProductoContratado.Finalizado"/>: la tuvo y ya no.
    /// </summary>
    Finalizada,
}

/// <summary>
/// ¿Qué estado tiene la línea Outbound (<see cref="LineaProductoContratado.OutboundGestionPropia"/>) del Producto
/// Contratado de un Tenant beneficiario? Es el plano comercial y nada más: no es autoridad de ninguna Query ni Command. La
/// consume la guarda de EP-4 (límites del Tenant TEC participante), que solo la consulta cuando el Tenant lleva el
/// marcador, y niega con cualquier estado distinto de <see cref="EstadoLineaOutbound.Vigente"/>.
///
/// <para>
/// Cuenta el Tenant beneficiario (el <c>TenantId</c> del Producto Contratado), nunca el Pagador TALVEG. Las filas
/// eliminadas no cuentan: si la única que hay está eliminada, el resultado es <see cref="EstadoLineaOutbound.SinLinea"/>.
/// Con varias filas manda la más fuerte: <see cref="EstadoLineaOutbound.Vigente"/>, después
/// <see cref="EstadoLineaOutbound.Programada"/> y después <see cref="EstadoLineaOutbound.Finalizada"/>.
/// </para>
///
/// <para>
/// Se lee bajo la identidad de runtime, con la política <c>aislamiento_tenant</c> de <c>ProductosContratados</c>: solo ve
/// las filas del Tenant activo. Preguntar por otro Tenant responde <see cref="EstadoLineaOutbound.SinLinea"/>, que para
/// la guarda de EP-4 es negar: falla cerrado. El filtro por Tenant y por filas no eliminadas se escribe aquí aunque lo
/// repitan el filtro global de la entidad y la RLS, para que la respuesta no dependa de que el contexto los aplique.
/// Bajo <c>cae_app_aprovisionamiento</c> la política RESTRICTIVE <c>aprovisionamiento_solo_lee_inbound</c> oculta las
/// filas que no son Inbound, así que ahí respondería <see cref="EstadoLineaOutbound.SinLinea"/>: no se consulta bajo esa
/// identidad.
/// </para>
///
/// <para>
/// Va fuera de MediatR y lee el contexto de persistencia de quien lo resuelve. Su consumidor previsto es un behavior del
/// pipeline. Un componente Blazor no lo llama directamente, sino a través de <c>PuertaAccesoDatos</c>; si no, compartiría
/// en paralelo el <c>DbContext</c> del circuito con el layout.
/// </para>
///
/// <para>
/// No hay backfill: un Tenant que opera Outbound sin fila de la línea responde <see cref="EstadoLineaOutbound.SinLinea"/>.
/// Que todo camino que da Outbound a un Tenant escriba la línea <see cref="LineaProductoContratado.OutboundGestionPropia"/>
/// es responsabilidad de Facturación.
/// </para>
/// </summary>
public interface IEstadoLineaOutbound
{
    Task<EstadoLineaOutbound> ObtenerEstadoLineaOutboundAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Atajo: <see cref="ObtenerEstadoLineaOutboundAsync"/> es <see cref="EstadoLineaOutbound.Vigente"/>.</summary>
    Task<bool> TieneLineaOutboundVigenteAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Lectura del Producto Contratado para <see cref="IEstadoLineaOutbound"/>. Sin tracking: la identidad de runtime
/// (<c>cae_app_runtime</c>) solo tiene SELECT sobre <c>ProductosContratados</c>.
/// </summary>
public interface ILineaOutboundQueryContext
{
    IQueryable<ProductoContratado> ProductosContratados { get; }
}

public sealed class EstadoLineaOutboundService(ILineaOutboundQueryContext contexto, TimeProvider reloj) : IEstadoLineaOutbound
{
    public async Task<EstadoLineaOutbound> ObtenerEstadoLineaOutboundAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime;
        var filas = await contexto.ProductosContratados
            .Where(p => p.TenantId == tenantId
                && p.Linea == LineaProductoContratado.OutboundGestionPropia
                && !p.EstaEliminado)
            .Select(p => new { p.Estado, p.VigenteDesdeUtc })
            .ToListAsync(cancellationToken);

        if (filas.Any(f => f.Estado == EstadoProductoContratado.Vigente && f.VigenteDesdeUtc <= ahora))
            return EstadoLineaOutbound.Vigente;
        if (filas.Any(f => f.Estado == EstadoProductoContratado.Vigente))
            return EstadoLineaOutbound.Programada;
        return filas.Count > 0 ? EstadoLineaOutbound.Finalizada : EstadoLineaOutbound.SinLinea;
    }

    public async Task<bool> TieneLineaOutboundVigenteAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await ObtenerEstadoLineaOutboundAsync(tenantId, cancellationToken) == EstadoLineaOutbound.Vigente;
}
