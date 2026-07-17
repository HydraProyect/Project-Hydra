using CaeManager.Domain.Centros;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class CentroRepository(CaeManagerDbContext dbContext) : ICentroRepository
{
    public Task<Centro?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Centros.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<bool> ExisteConNombreEnClienteAsync(
        Guid clienteId, string nombre, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        dbContext.Centros.AnyAsync(
            c => c.ClienteId == clienteId && c.Nombre.ToUpper() == nombre.ToUpper() && (excluirId == null || c.Id != excluirId),
            cancellationToken);

    public void Agregar(Centro centro) => dbContext.Centros.Add(centro);
}
