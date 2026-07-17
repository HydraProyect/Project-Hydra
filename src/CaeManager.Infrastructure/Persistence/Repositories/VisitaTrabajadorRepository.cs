using CaeManager.Domain.Visitas;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class VisitaTrabajadorRepository(CaeManagerDbContext dbContext) : IVisitaTrabajadorRepository
{
    public async Task<IReadOnlyList<VisitaTrabajador>> ObtenerPorVisitaAsync(Guid visitaId, CancellationToken cancellationToken = default) =>
        await dbContext.Set<VisitaTrabajador>().Where(vt => vt.VisitaId == visitaId).ToListAsync(cancellationToken);

    public void Agregar(VisitaTrabajador visitaTrabajador) => dbContext.Set<VisitaTrabajador>().Add(visitaTrabajador);

    public void Eliminar(VisitaTrabajador visitaTrabajador) => dbContext.Set<VisitaTrabajador>().Remove(visitaTrabajador);
}
