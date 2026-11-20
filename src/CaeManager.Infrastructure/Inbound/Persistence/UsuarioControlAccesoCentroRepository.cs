using CaeManager.Domain.Inbound.ControlAcceso;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Inbound.Persistence;

public class UsuarioControlAccesoCentroRepository(CaeManagerDbContext dbContext) : IUsuarioControlAccesoCentroRepository
{
    public async Task<IReadOnlyList<UsuarioControlAccesoCentro>> ObtenerDeUsuarioAsync(
        Guid usuarioId, CancellationToken cancellationToken = default) =>
        await dbContext.UsuariosControlAccesoCentro.Where(a => a.UsuarioId == usuarioId).ToListAsync(cancellationToken);

    public void Agregar(UsuarioControlAccesoCentro asignacion) => dbContext.UsuariosControlAccesoCentro.Add(asignacion);

    public void Retirar(UsuarioControlAccesoCentro asignacion) => dbContext.UsuariosControlAccesoCentro.Remove(asignacion);
}
