using CaeManager.Domain.Soporte;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class RegistroActividadSoporteRepository(CaeManagerDbContext dbContext) : IRegistroActividadSoporteRepository
{
    public void Agregar(RegistroActividadSoporte registro) => dbContext.RegistrosActividadSoporte.Add(registro);

    public async Task<IReadOnlyList<RegistroActividadSoporte>> ObtenerPorDelegacionAsync(
        Guid delegacionTenantId, CancellationToken cancellationToken = default) =>
        await dbContext.RegistrosActividadSoporte
            .Where(r => r.DelegacionTenantId == delegacionTenantId)
            .OrderBy(r => r.OcurridaEnUtc)
            .ToListAsync(cancellationToken);
}
