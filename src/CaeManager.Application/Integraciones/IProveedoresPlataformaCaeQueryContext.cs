using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Integraciones;

public interface IProveedoresPlataformaCaeQueryContext
{
    IQueryable<ProveedorPlataformaCae> ProveedoresPlataformaCae { get; }
    IQueryable<DominioProveedorPlataformaCae> DominiosProveedorPlataformaCae { get; }
}
