using CaeManager.Domain.Inbound.AccesoFisico;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Inbound.Persistence;

/// <summary>
/// Sin <c>Eliminar</c> físico: el rol de runtime no tiene DELETE sobre <c>CredencialesAcceso</c>. Una credencial que deja
/// de valer se revoca o caduca; la fila queda como historia de quién tuvo carné y cuándo.
/// </summary>
public class CredencialAccesoRepository(CaeManagerDbContext dbContext) : ICredencialAccesoRepository
{
    public Task<CredencialAcceso?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.CredencialesAcceso.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<CredencialAcceso?> ObtenerEmitidaDelTrabajadorAsync(
        Guid tenantOrigenId, Guid trabajadorOrigenId, CancellationToken cancellationToken = default) =>
        dbContext.CredencialesAcceso.FirstOrDefaultAsync(
            c => c.TenantOrigenId == tenantOrigenId && c.TrabajadorOrigenId == trabajadorOrigenId
                 && c.Estado == EstadoCredencialAcceso.Emitida,
            cancellationToken);

    public void Agregar(CredencialAcceso credencial) => dbContext.CredencialesAcceso.Add(credencial);
}
