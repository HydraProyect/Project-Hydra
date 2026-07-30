using CaeManager.Domain.Incidencias;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class IncidenciaRepository(CaeManagerDbContext dbContext) : IIncidenciaRepository
{
    public Task<Incidencia?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Incidencias.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

    public void Agregar(Incidencia incidencia) => dbContext.Incidencias.Add(incidencia);
}
