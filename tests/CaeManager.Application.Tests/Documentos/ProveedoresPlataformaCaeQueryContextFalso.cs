using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Tests.Documentos;

public class ProveedoresPlataformaCaeQueryContextFalso : IProveedoresPlataformaCaeQueryContext
{
    public List<ProveedorPlataformaCae> ListaProveedores { get; } = [];
    public List<DominioProveedorPlataformaCae> ListaDominios { get; } = [];

    public IQueryable<ProveedorPlataformaCae> ProveedoresPlataformaCae => new TestAsyncQueryable<ProveedorPlataformaCae>(ListaProveedores.AsQueryable());
    public IQueryable<DominioProveedorPlataformaCae> DominiosProveedorPlataformaCae => new TestAsyncQueryable<DominioProveedorPlataformaCae>(ListaDominios.AsQueryable());
}
