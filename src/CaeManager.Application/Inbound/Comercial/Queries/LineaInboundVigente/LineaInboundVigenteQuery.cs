using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Comercial;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Inbound.Comercial.Queries.LineaInboundVigente;

/// <summary>
/// ¿Tiene el Tenant activo, como Tenant beneficiario, un Producto Contratado de la línea Inbound vigente? Es el plano
/// comercial y nada más: decide si la interfaz enseña la sección «Inbound» (menú lateral y destinos de la paleta), nunca
/// la autoridad de una Query o un Command de Inbound, que sigue siendo la suya, ni el gate de la lectura entre Tenants,
/// que lo impone la base (DN-7, <c>app_inbound_tenant_contratista_del_contrato</c>).
///
/// <para>
/// El criterio es exactamente el de ese gate de la base: línea <see cref="LineaProductoContratado.Inbound"/>, estado
/// <see cref="EstadoProductoContratado.Vigente"/>, no eliminado y con el inicio de la vigencia ya alcanzado. Cuenta el
/// Tenant beneficiario (el <c>TenantId</c> del Producto Contratado), nunca el Pagador TALVEG. Un Producto Contratado dado
/// de baja o corregido queda <see cref="EstadoProductoContratado.Finalizado"/> y no cuenta; no hay otro estado (el cobro
/// vive en <c>Tenant.EstadoComercial</c> y no entra aquí). La diferencia con la base es el reloj: aquí el de la aplicación,
/// allí <c>clock_timestamp()</c>; un desfase solo adelanta o retrasa unos segundos que la sección aparezca.
/// </para>
///
/// <para>
/// Falla cerrado: sin Tenant activo responde <c>false</c>. El filtro por el Tenant activo y por las filas no eliminadas
/// se escribe aquí aunque lo repitan el filtro global de la entidad y la política <c>aislamiento_tenant</c>, para que la
/// respuesta no dependa de que el contexto de lectura los aplique.
/// </para>
/// </summary>
[AreaDelEncargo("Comercial")]
public record LineaInboundVigenteQuery : IRequest<bool>;

public class LineaInboundVigenteQueryHandler(ILineaInboundQueryContext contexto, ITenantActual tenantActual, TimeProvider reloj)
    : IRequestHandler<LineaInboundVigenteQuery, bool>
{
    public async Task<bool> Handle(LineaInboundVigenteQuery request, CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } tenantId)
            return false;

        var ahora = reloj.GetUtcNow().UtcDateTime;
        return await contexto.ProductosContratados.AnyAsync(p =>
                p.TenantId == tenantId
                && p.Linea == LineaProductoContratado.Inbound
                && p.Estado == EstadoProductoContratado.Vigente
                && !p.EstaEliminado
                && p.VigenteDesdeUtc <= ahora,
            cancellationToken);
    }
}
