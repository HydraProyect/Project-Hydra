using CaeManager.Domain.Proyectos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ProyectoRequisitoRepository(CaeManagerDbContext dbContext) : IProyectoRequisitoRepository
{
    public async Task<IReadOnlyList<ProyectoRequisito>> ListarDeProyectoAsync(Guid proyectoId, CancellationToken cancellationToken = default) =>
        await dbContext.ProyectosRequisitos.Where(r => r.ProyectoId == proyectoId).ToListAsync(cancellationToken);

    public void Agregar(ProyectoRequisito requisito) => dbContext.ProyectosRequisitos.Add(requisito);

    public void Eliminar(ProyectoRequisito requisito) => dbContext.ProyectosRequisitos.Remove(requisito);
}

public class PlantillaRequisitosEventoRepository(CaeManagerDbContext dbContext) : IPlantillaRequisitosEventoRepository
{
    public Task<PlantillaRequisitosEvento?> ObtenerConTiposAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.PlantillasRequisitosEvento.Include(p => p.Tipos).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<bool> ExisteNombreParaClienteAsync(Guid clienteId, string nombre, CancellationToken cancellationToken = default)
    {
        var normalizado = nombre.Trim();
        return dbContext.PlantillasRequisitosEvento.AnyAsync(p => p.ClienteId == clienteId && p.Nombre == normalizado, cancellationToken);
    }

    public void Agregar(PlantillaRequisitosEvento plantilla) => dbContext.PlantillasRequisitosEvento.Add(plantilla);
}
