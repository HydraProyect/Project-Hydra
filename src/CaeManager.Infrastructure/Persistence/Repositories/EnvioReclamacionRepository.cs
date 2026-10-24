using CaeManager.Domain.Reclamaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class EnvioReclamacionRepository(CaeManagerDbContext dbContext) : IEnvioReclamacionRepository
{
    public Task<EnvioReclamacion?> ObtenerPendienteAsync(
        Guid usuarioId, string huella, DateTime iniciadoDesdeUtc, CancellationToken cancellationToken) =>
        dbContext.EnviosReclamacion
            .Where(e => e.UsuarioId == usuarioId && e.Huella == huella)
            .Where(e => e.ReclamacionDocumentalId == null && e.IniciadoEnUtc >= iniciadoDesdeUtc)
            .OrderByDescending(e => e.IniciadoEnUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public void Agregar(EnvioReclamacion envio) => dbContext.EnviosReclamacion.Add(envio);
}
