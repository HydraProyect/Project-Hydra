using CaeManager.Application.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Clientes;

namespace CaeManager.Application.Tests.Plantillas;

public class ClientesQueryContextFalso : IClientesQueryContext
{
    public List<Cliente> ListaClientes { get; } = [];

    public IQueryable<Cliente> Clientes => new TestAsyncQueryable<Cliente>(ListaClientes.AsQueryable());
}
