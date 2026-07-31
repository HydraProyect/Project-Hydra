using CaeManager.Domain.Tenants;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>Fake en memoria — los handlers de Application se prueban sin base de datos (ver CODING_STANDARDS.md).</summary>
public class DelegacionTenantRepositorioFalso : IDelegacionTenantRepository
{
    public List<DelegacionTenant> Delegaciones { get; } = [];

    public Task<DelegacionTenant?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Delegaciones.FirstOrDefault(d => d.Id == id));

    public Task<bool> ExisteActivaAsync(Guid tenantConsultoraId, Guid tenantClienteId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Delegaciones.Any(d =>
            d.TenantConsultoraId == tenantConsultoraId && d.TenantClienteId == tenantClienteId && d.Activa));

    public void Agregar(DelegacionTenant delegacion) => Delegaciones.Add(delegacion);
}

public class AsignacionOperadorDelegadoRepositorioFalso : IAsignacionOperadorDelegadoRepository
{
    public List<AsignacionOperadorDelegado> Asignaciones { get; } = [];

    public Task<AsignacionOperadorDelegado?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Asignaciones.FirstOrDefault(a => a.Id == id));

    public Task<bool> ExisteAsync(Guid delegacionTenantId, Guid usuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Asignaciones.Any(a => a.DelegacionTenantId == delegacionTenantId && a.UsuarioId == usuarioId));

    public void Agregar(AsignacionOperadorDelegado asignacion) => Asignaciones.Add(asignacion);

    public void Eliminar(AsignacionOperadorDelegado asignacion) => Asignaciones.Remove(asignacion);
}
