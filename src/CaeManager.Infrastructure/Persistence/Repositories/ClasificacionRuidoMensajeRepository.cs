using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ClasificacionRuidoMensajeRepository(CaeManagerDbContext dbContext) : IClasificacionRuidoMensajeRepository
{
    public void Agregar(ClasificacionRuidoMensaje clasificacion) => dbContext.ClasificacionesRuidoMensaje.Add(clasificacion);

    public Task<ClasificacionRuidoMensaje?> ObtenerPorMensajeIdAsync(Guid mensajeId, CancellationToken cancellationToken = default) =>
        dbContext.ClasificacionesRuidoMensaje.FirstOrDefaultAsync(c => c.MensajeId == mensajeId, cancellationToken);
}
