using CaeManager.Domain.Configuracion;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class FiltroGuardadoRepository(CaeManagerDbContext dbContext) : IFiltroGuardadoRepository
{
    public Task<FiltroGuardado?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.FiltrosGuardados.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

    public void Agregar(FiltroGuardado filtro) => dbContext.FiltrosGuardados.Add(filtro);

    public void Eliminar(FiltroGuardado filtro) => dbContext.FiltrosGuardados.Remove(filtro);
}
