using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class UltimoResumenNotificacionPlataformaRepository(CaeManagerDbContext dbContext) : IUltimoResumenNotificacionPlataformaRepository
{
    public Task<UltimoResumenNotificacionPlataforma?> ObtenerAsync(
        Guid clienteId, Guid proveedorPlataformaCaeId, CancellationToken cancellationToken = default) =>
        dbContext.UltimosResumenesNotificacionPlataforma
            .FirstOrDefaultAsync(u => u.ClienteId == clienteId && u.ProveedorPlataformaCaeId == proveedorPlataformaCaeId, cancellationToken);

    public void Agregar(UltimoResumenNotificacionPlataforma resumen) => dbContext.UltimosResumenesNotificacionPlataforma.Add(resumen);
}
