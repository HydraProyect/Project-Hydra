using CaeManager.Domain.Asignaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class AsignacionRepository(CaeManagerDbContext dbContext) : IAsignacionRepository
{
    public Task<Asignacion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Asignaciones.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<bool> ExisteActivaAsync(Guid trabajadorId, Guid centroId, CancellationToken cancellationToken = default) =>
        dbContext.Asignaciones.AnyAsync(
            a => a.TrabajadorId == trabajadorId && a.CentroId == centroId && a.FechaBaja == null,
            cancellationToken);

    public void Agregar(Asignacion asignacion) => dbContext.Asignaciones.Add(asignacion);
}
