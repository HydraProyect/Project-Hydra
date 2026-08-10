using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ClasificacionRuidoMensajeRepository(CaeManagerDbContext dbContext) : IClasificacionRuidoMensajeRepository
{
    public void Agregar(ClasificacionRuidoMensaje clasificacion) => dbContext.ClasificacionesRuidoMensaje.Add(clasificacion);
}
