using CaeManager.Domain.Proyectos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ProyectoTecnicoRepository(CaeManagerDbContext dbContext) : IProyectoTecnicoRepository
{
    public Task<ProyectoTecnico?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ProyectosTecnicos.FirstOrDefaultAsync(pt => pt.Id == id, cancellationToken);

    public Task<bool> ExisteActivoAsync(Guid proyectoId, Guid trabajadorId, CancellationToken cancellationToken = default) =>
        dbContext.ProyectosTecnicos.AnyAsync(
            pt => pt.ProyectoId == proyectoId && pt.TrabajadorId == trabajadorId && pt.FechaBaja == null,
            cancellationToken);

    public void Agregar(ProyectoTecnico proyectoTecnico) => dbContext.ProyectosTecnicos.Add(proyectoTecnico);
}
