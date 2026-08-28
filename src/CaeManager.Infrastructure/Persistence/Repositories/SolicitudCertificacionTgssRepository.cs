using CaeManager.Domain.Blindaje42;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class SolicitudCertificacionTgssRepository(CaeManagerDbContext dbContext) : ISolicitudCertificacionTgssRepository
{
    public Task<SolicitudCertificacionTgss?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.SolicitudesCertificacionTgss.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public void Agregar(SolicitudCertificacionTgss solicitud) =>
        dbContext.SolicitudesCertificacionTgss.Add(solicitud);
}
