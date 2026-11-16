using CaeManager.Domain.Cumplimiento;
using CaeManager.Domain.Cumplimiento.Subencargados;

namespace CaeManager.Application.Cumplimiento;

/// <summary>
/// Habilitada si el Tenant propietario tiene una instrucción vigente y no ha excluido la IA objetando un subencargado
/// activo que lo admite (#2010, D-7). La objeción no revoca la instrucción: solo la deja sin efecto mientras ese
/// subencargado esté activo (antes de activarse ese Tenant no lo usa; retirado, no lo usa nadie). Las dos lecturas
/// van acotadas al Tenant actual por filtro y RLS, así que, si la RLS oculta una, oculta también la otra y el
/// resultado falla cerrado. <c>cae_app_runtime</c> y <c>cae_app_soporte</c> (Sesión Privilegiada de Soporte TALVEG)
/// tienen SELECT sobre las tres tablas, así que el gate responde también dentro de una Sesión Privilegiada.
/// </summary>
public class InstruccionTratamientoIaService(
    IInstruccionTratamientoIaTenantPropietarioRepository repositorio,
    ISubencargadoRepository subencargados)
    : IInstruccionTratamientoIaService
{
    public async Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await TieneInstruccionVigenteAsync(tenantId, cancellationToken)
        && !await subencargados.ExcluyeConmutadorAsync(tenantId, ConmutadorExclusionSubencargado.Ia, cancellationToken);

    public async Task<bool> TieneInstruccionVigenteAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await repositorio.ObtenerVigenteAsync(tenantId, cancellationToken) is not null;
}
