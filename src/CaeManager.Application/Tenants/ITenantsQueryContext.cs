using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;

namespace CaeManager.Application.Tenants;

public interface ITenantsQueryContext
{
    IQueryable<Tenant> Tenants { get; }
    IQueryable<DelegacionTenant> DelegacionesTenant { get; }
    /// <summary>
    /// Solo las asignaciones que conceden su rol hoy: las revocadas (P8) se
    /// conservan en base de datos pero no se exponen a ningún lector.
    /// </summary>
    IQueryable<AsignacionOperadorDelegado> AsignacionesOperadorDelegado { get; }
    IQueryable<RegistroActividadSoporte> RegistrosActividadSoporte { get; }
}
