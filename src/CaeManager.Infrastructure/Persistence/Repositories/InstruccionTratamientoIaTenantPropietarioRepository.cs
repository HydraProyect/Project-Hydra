using CaeManager.Domain.Cumplimiento;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class InstruccionTratamientoIaTenantPropietarioRepository(CaeManagerDbContext dbContext)
    : IInstruccionTratamientoIaTenantPropietarioRepository
{
    public Task<InstruccionTratamientoIaTenantPropietario?> ObtenerVigenteAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        dbContext.InstruccionesTratamientoIaTenantPropietario
            .Where(i => i.TenantId == tenantId && i.RevocadaEnUtc == null)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<InstruccionTratamientoIaTenantPropietario>> ObtenerHistoricoAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await dbContext.InstruccionesTratamientoIaTenantPropietario
            .Where(i => i.TenantId == tenantId)
            .OrderByDescending(i => i.FechaAceptacionUtc)
            .ToListAsync(cancellationToken);

    public Task<InstruccionTratamientoIaTenantPropietario?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.InstruccionesTratamientoIaTenantPropietario.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

    public void Agregar(InstruccionTratamientoIaTenantPropietario instruccion) =>
        dbContext.InstruccionesTratamientoIaTenantPropietario.Add(instruccion);
}
