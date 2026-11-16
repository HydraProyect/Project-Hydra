using CaeManager.Domain.Comercial;

namespace CaeManager.Application.Inbound.Comercial;

/// <summary>
/// Lectura del Producto Contratado para saber si el Tenant activo, que es el Tenant beneficiario, tiene la línea Inbound
/// vigente. Sin tracking: la identidad de runtime (<c>cae_app_runtime</c>) solo tiene SELECT sobre
/// <c>ProductosContratados</c>, y la política <c>aislamiento_tenant</c> y el filtro global de la entidad la acotan al
/// Tenant activo y a las filas no eliminadas.
/// </summary>
public interface ILineaInboundQueryContext
{
    IQueryable<ProductoContratado> ProductosContratados { get; }
}
