using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ClienteRepository(CaeManagerDbContext dbContext) : IClienteRepository
{
    public Task<Cliente?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Clientes.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<bool> ExisteConRazonSocialAsync(string razonSocial, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        dbContext.Clientes.AnyAsync(
            c => c.RazonSocial.ToUpper() == razonSocial.ToUpper() && (excluirId == null || c.Id != excluirId),
            cancellationToken);

    public Task<bool> ExisteConCifAsync(string cif, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        dbContext.Clientes.AnyAsync(
            c => c.Cif.ToUpper() == cif.ToUpper() && (excluirId == null || c.Id != excluirId),
            cancellationToken);

    public Task<bool> TieneCentrosActivosAsync(Guid clienteId, CancellationToken cancellationToken = default) =>
        dbContext.Set<Centro>().AnyAsync(c => c.ClienteId == clienteId, cancellationToken);

    public void Agregar(Cliente cliente) => dbContext.Clientes.Add(cliente);
}
