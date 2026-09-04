using CaeManager.Domain.Cumplimiento;

namespace CaeManager.Application.Cumplimiento;

public class InstruccionTratamientoIaService(IInstruccionTratamientoIaTenantPropietarioRepository repositorio)
    : IInstruccionTratamientoIaService
{
    public async Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await repositorio.ObtenerVigenteAsync(tenantId, cancellationToken) is not null;
}
