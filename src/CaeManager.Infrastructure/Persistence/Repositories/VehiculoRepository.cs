using CaeManager.Domain.Vehiculos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class VehiculoRepository(CaeManagerDbContext dbContext) : IVehiculoRepository
{
    public Task<Vehiculo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Vehiculos.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);

    public Task<bool> ExisteConMatriculaAsync(string numeroPlaca, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        dbContext.Vehiculos.AnyAsync(
            v => v.NumeroPlaca == numeroPlaca.ToUpper() && (excluirId == null || v.Id != excluirId),
            cancellationToken);

    public void Agregar(Vehiculo vehiculo) => dbContext.Vehiculos.Add(vehiculo);
}
