using CaeManager.Application.Tenants;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;

namespace CaeManager.Application.Tests.Comercial;

/// <summary>
/// Mismo envoltorio <c>TestAsyncQueryable</c> que
/// <c>IntegracionesQueryContextFalso</c> (ver su comentario) — los handlers
/// de Comercial usan AnyAsync/SingleOrDefaultAsync de EF Core, que un
/// List.AsQueryable() normal no soporta.
/// </summary>
public class TenantsQueryContextFalso : ITenantsQueryContext
{
    public List<Tenant> ListaTenants { get; } = [];
    public List<DelegacionTenant> ListaDelegacionesTenant { get; } = [];
    public List<AsignacionOperadorDelegado> ListaAsignacionesOperadorDelegado { get; } = [];
    public List<RegistroActividadSoporte> ListaRegistrosActividadSoporte { get; } = [];

    public IQueryable<Tenant> Tenants => new TestAsyncQueryable<Tenant>(ListaTenants.AsQueryable());
    public IQueryable<DelegacionTenant> DelegacionesTenant => new TestAsyncQueryable<DelegacionTenant>(ListaDelegacionesTenant.AsQueryable());
    public IQueryable<AsignacionOperadorDelegado> AsignacionesOperadorDelegado => new TestAsyncQueryable<AsignacionOperadorDelegado>(ListaAsignacionesOperadorDelegado.AsQueryable());
    public IQueryable<RegistroActividadSoporte> RegistrosActividadSoporte => new TestAsyncQueryable<RegistroActividadSoporte>(ListaRegistrosActividadSoporte.AsQueryable());
}
