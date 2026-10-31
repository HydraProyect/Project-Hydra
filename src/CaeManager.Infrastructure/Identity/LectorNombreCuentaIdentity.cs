using CaeManager.Application.Usuarios.Queries.ObtenerNombreCuenta;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Identity;

public sealed class LectorNombreCuentaIdentity(CaeManagerDbContext contexto) : ILectorNombreCuenta
{
    public Task<NombreCuentaDto?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken) =>
        contexto.Users.AsNoTracking()
            .Where(usuario => usuario.Id == usuarioId)
            .Select(usuario => new NombreCuentaDto(usuario.NombreCompleto, usuario.Email))
            .FirstOrDefaultAsync(cancellationToken);
}
