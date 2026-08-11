using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class DetalleSugerenciaGestionCorreoRepository(CaeManagerDbContext dbContext) : IDetalleSugerenciaGestionCorreoRepository
{
    public Task<DetalleSugerenciaGestionCorreo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.DetallesSugerenciaGestionCorreo.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
}
