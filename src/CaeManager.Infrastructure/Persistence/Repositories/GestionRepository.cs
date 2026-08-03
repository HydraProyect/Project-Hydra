using CaeManager.Domain.Gestiones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class GestionRepository(CaeManagerDbContext dbContext) : IGestionRepository
{
    public Task<Gestion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Gestiones.FirstOrDefaultAsync(g => g.Id == id, cancellationToken);

    public void Agregar(Gestion gestion) => dbContext.Gestiones.Add(gestion);
}
