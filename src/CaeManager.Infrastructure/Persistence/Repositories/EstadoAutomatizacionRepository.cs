using CaeManager.Domain.Configuracion;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class EstadoAutomatizacionRepository(CaeManagerDbContext dbContext) : IEstadoAutomatizacionRepository
{
    public Task<EstadoAutomatizacion?> ObtenerPorTrabajoAsync(string trabajoId, CancellationToken cancellationToken = default) =>
        dbContext.EstadosAutomatizacion.SingleOrDefaultAsync(e => e.TrabajoId == trabajoId, cancellationToken);

    public async Task<IReadOnlyList<EstadoAutomatizacion>> ObtenerTodosAsync(CancellationToken cancellationToken = default) =>
        await dbContext.EstadosAutomatizacion.ToListAsync(cancellationToken);

    public void Agregar(EstadoAutomatizacion estado) => dbContext.EstadosAutomatizacion.Add(estado);
}
