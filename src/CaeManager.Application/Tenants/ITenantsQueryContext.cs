using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;

namespace CaeManager.Application.Tenants;

public interface ITenantsQueryContext
{
    IQueryable<Tenant> Tenants { get; }
    IQueryable<DelegacionTenant> DelegacionesTenant { get; }
    IQueryable<AsignacionOperadorDelegado> AsignacionesOperadorDelegado { get; }
    IQueryable<RegistroActividadSoporte> RegistrosActividadSoporte { get; }
}
