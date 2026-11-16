using CaeManager.Domain.Cumplimiento;
using CaeManager.Domain.Cumplimiento.Subencargados;

namespace CaeManager.Application.Cumplimiento;

/// <summary>
/// Habilitada si el Tenant propietario tiene una instrucción vigente y no ha excluido la IA objetando un subencargado
/// que lo admite (#2010, D-7). La objeción no toca la instrucción: solo la deja sin efecto mientras ese subencargado
/// no se retire. Las dos lecturas van acotadas al Tenant actual por filtro y RLS, así que, si la RLS oculta una,
/// oculta también la otra y el resultado falla cerrado.
/// </summary>
public class InstruccionTratamientoIaService(
    IInstruccionTratamientoIaTenantPropietarioRepository repositorio,
    ISubencargadoRepository subencargados)
    : IInstruccionTratamientoIaService
{
    public async Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await repositorio.ObtenerVigenteAsync(tenantId, cancellationToken) is not null
        && !await subencargados.ExcluyeConmutadorAsync(tenantId, ConmutadorExclusionSubencargado.Ia, cancellationToken);
}
