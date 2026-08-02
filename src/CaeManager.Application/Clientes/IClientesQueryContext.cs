using CaeManager.Domain.Clientes;

namespace CaeManager.Application.Clientes;

public interface IClientesQueryContext
{
    IQueryable<Cliente> Clientes { get; }
}
