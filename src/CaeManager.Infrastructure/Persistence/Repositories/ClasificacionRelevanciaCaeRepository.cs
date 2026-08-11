using CaeManager.Domain.Comunicaciones;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ClasificacionRelevanciaCaeRepository(CaeManagerDbContext dbContext) : IClasificacionRelevanciaCaeRepository
{
    public void Agregar(ClasificacionRelevanciaCae clasificacion) => dbContext.ClasificacionesRelevanciaCae.Add(clasificacion);

    public Task<ClasificacionRelevanciaCae?> ObtenerPorConversacionIdAsync(Guid conversacionId, CancellationToken cancellationToken = default) =>
        dbContext.ClasificacionesRelevanciaCae.FirstOrDefaultAsync(c => c.ConversacionId == conversacionId, cancellationToken);
}
