using CaeManager.Domain.Visitas;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class VisitaRepository(CaeManagerDbContext dbContext) : IVisitaRepository
{
    public Task<Visita?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Visitas.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);

    public void Agregar(Visita visita) => dbContext.Visitas.Add(visita);
}
