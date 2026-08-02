using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class SuscripcionWebhookRepository(CaeManagerDbContext dbContext) : ISuscripcionWebhookRepository
{
    public Task<SuscripcionWebhook?> ObtenerPorConexionAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default) =>
        dbContext.SuscripcionesWebhook.FirstOrDefaultAsync(s => s.ConexionIntegracionId == conexionIntegracionId, cancellationToken);

    public async Task<IReadOnlyList<SuscripcionWebhook>> ObtenerProximasAExpirarAsync(
        TimeSpan dentroDe, CancellationToken cancellationToken = default)
    {
        var limite = DateTime.UtcNow + dentroDe;
        return await dbContext.SuscripcionesWebhook
            .Where(s => s.FechaExpiracionUtc <= limite)
            .ToListAsync(cancellationToken);
    }

    public void Agregar(SuscripcionWebhook suscripcion) => dbContext.SuscripcionesWebhook.Add(suscripcion);
}
