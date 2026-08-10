using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ClasificacionRuidoDetalleGestionRepository(CaeManagerDbContext dbContext) : IClasificacionRuidoDetalleGestionRepository
{
    public void Agregar(ClasificacionRuidoDetalleGestion clasificacion) => dbContext.ClasificacionesRuidoDetalleGestion.Add(clasificacion);

    public Task<ClasificacionRuidoDetalleGestion?> ObtenerPorDetalleIdAsync(
        Guid detalleSugerenciaGestionCorreoId, CancellationToken cancellationToken = default) =>
        dbContext.ClasificacionesRuidoDetalleGestion
            .FirstOrDefaultAsync(c => c.DetalleSugerenciaGestionCorreoId == detalleSugerenciaGestionCorreoId, cancellationToken);
}
