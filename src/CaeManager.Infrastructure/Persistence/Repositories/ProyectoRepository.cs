using CaeManager.Domain.Proyectos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ProyectoRepository(CaeManagerDbContext dbContext) : IProyectoRepository
{
    public Task<Proyecto?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Proyectos.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<bool> ExisteNombreParaClienteAsync(Guid clienteId, string nombre, Guid? excluirId = null, CancellationToken cancellationToken = default)
    {
        var nombreNormalizado = nombre.Trim();
        return dbContext.Proyectos.AnyAsync(
            p => p.ClienteId == clienteId
              && p.Nombre == nombreNormalizado
              && (excluirId == null || p.Id != excluirId),
            cancellationToken);
    }

    public void Agregar(Proyecto proyecto) => dbContext.Proyectos.Add(proyecto);

    public void Eliminar(Proyecto proyecto) => dbContext.Proyectos.Remove(proyecto);
}
