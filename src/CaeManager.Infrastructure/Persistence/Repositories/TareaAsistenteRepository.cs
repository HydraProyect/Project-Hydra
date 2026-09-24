using CaeManager.Domain.AsistenteIa;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class TareaAsistenteRepository(CaeManagerDbContext dbContext) : ITareaAsistenteRepository
{
    public void Agregar(TareaAsistente tarea) => dbContext.TareasAsistente.Add(tarea);

    public Task<TareaAsistente?> ObtenerDePersonaAsync(Guid tareaId, Guid actorRealUsuarioId, CancellationToken cancellationToken = default) =>
        dbContext.TareasAsistente
            .Include(t => t.Turnos)
            .Include(t => t.Pasos)
            .AsSplitQuery()
            .FirstOrDefaultAsync(t => t.Id == tareaId && t.ActorRealUsuarioId == actorRealUsuarioId, cancellationToken);
}
