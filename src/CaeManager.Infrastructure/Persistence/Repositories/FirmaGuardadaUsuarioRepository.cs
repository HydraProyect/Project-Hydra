using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class FirmaGuardadaUsuarioRepository(CaeManagerDbContext dbContext) : IFirmaGuardadaUsuarioRepository
{
    public void Agregar(FirmaGuardadaUsuario firma) => dbContext.FirmasGuardadasUsuario.Add(firma);

    public async Task<FirmaGuardadaUsuario?> ObtenerPorUsuarioAsync(
        Guid usuarioId, CancellationToken cancellationToken = default) =>
        await dbContext.FirmasGuardadasUsuario.FirstOrDefaultAsync(f => f.UsuarioId == usuarioId, cancellationToken);
}
