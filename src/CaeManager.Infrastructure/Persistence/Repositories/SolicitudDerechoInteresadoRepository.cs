using CaeManager.Domain.DerechosInteresado;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class SolicitudDerechoInteresadoRepository(CaeManagerDbContext dbContext) : ISolicitudDerechoInteresadoRepository
{
    public Task<SolicitudDerechoInteresado?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.SolicitudesDerechoInteresado.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    // shortcut: sin paginación; el registro de un Tenant son pocas solicitudes al año. Paginar cuando llegue la
    // pantalla del registro o un Tenant pase de unos cientos.
    public async Task<IReadOnlyList<SolicitudDerechoInteresado>> ListarAsync(CancellationToken cancellationToken) =>
        await dbContext.SolicitudesDerechoInteresado
            .AsNoTracking()
            .OrderBy(s => s.PlazoVencimiento)
            .ThenBy(s => s.RegistradaEnUtc)
            .ToListAsync(cancellationToken);

    public void Agregar(SolicitudDerechoInteresado solicitud) => dbContext.SolicitudesDerechoInteresado.Add(solicitud);
}
