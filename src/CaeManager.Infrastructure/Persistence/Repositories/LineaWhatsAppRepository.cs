using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class LineaWhatsAppRepository(CaeManagerDbContext dbContext) : ILineaWhatsAppRepository
{
    public Task<LineaWhatsApp?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.LineasWhatsApp
            .Include(l => l.MiembrosPool)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken);

    public Task<LineaWhatsApp?> ObtenerPorConexionAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default) =>
        dbContext.LineasWhatsApp
            .Include(l => l.MiembrosPool)
            .FirstOrDefaultAsync(l => l.ConexionIntegracionId == conexionIntegracionId, cancellationToken);

    public void Agregar(LineaWhatsApp linea) => dbContext.LineasWhatsApp.Add(linea);
}
