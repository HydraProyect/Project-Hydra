using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class TrabajadorRepository(CaeManagerDbContext dbContext) : ITrabajadorRepository
{
    public Task<Trabajador?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Trabajadores.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public Task<bool> ExisteConDniAsync(string dni, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        dbContext.Trabajadores.AnyAsync(
            t => t.Dni == dni && (excluirId == null || t.Id != excluirId),
            cancellationToken);

    public void Agregar(Trabajador trabajador) => dbContext.Trabajadores.Add(trabajador);
}
