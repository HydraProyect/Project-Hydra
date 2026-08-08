using CaeManager.Domain.Cumplimiento;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class AceptacionTerminosRepository(CaeManagerDbContext dbContext) : IAceptacionTerminosRepository
{
    public Task<bool> ExisteParaVersionAsync(Guid usuarioId, string versionDocumento, CancellationToken cancellationToken = default) =>
        dbContext.AceptacionesTerminos.AnyAsync(a => a.UsuarioId == usuarioId && a.VersionDocumento == versionDocumento, cancellationToken);

    public void Agregar(AceptacionTerminos aceptacion) => dbContext.AceptacionesTerminos.Add(aceptacion);
}
