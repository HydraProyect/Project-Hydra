using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class DeteccionTrabajadorRepository(CaeManagerDbContext dbContext) : IDeteccionTrabajadorRepository
{
    public Task<DeteccionTrabajador?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.DeteccionesTrabajador.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    public void Agregar(DeteccionTrabajador deteccion) => dbContext.DeteccionesTrabajador.Add(deteccion);
}
