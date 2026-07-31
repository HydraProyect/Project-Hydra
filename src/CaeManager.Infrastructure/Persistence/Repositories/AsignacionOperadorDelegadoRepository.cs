using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class AsignacionOperadorDelegadoRepository(CaeManagerDbContext dbContext) : IAsignacionOperadorDelegadoRepository
{
    public Task<AsignacionOperadorDelegado?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.AsignacionesOperadorDelegado.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<bool> ExisteAsync(Guid delegacionTenantId, Guid usuarioId, CancellationToken cancellationToken = default) =>
        dbContext.AsignacionesOperadorDelegado.AnyAsync(
            a => a.DelegacionTenantId == delegacionTenantId && a.UsuarioId == usuarioId, cancellationToken);

    public Task<AsignacionOperadorDelegado?> ObtenerPorDelegacionYUsuarioAsync(
        Guid delegacionTenantId, Guid usuarioId, CancellationToken cancellationToken = default) =>
        dbContext.AsignacionesOperadorDelegado.FirstOrDefaultAsync(
            a => a.DelegacionTenantId == delegacionTenantId && a.UsuarioId == usuarioId, cancellationToken);

    public void Agregar(AsignacionOperadorDelegado asignacion) => dbContext.AsignacionesOperadorDelegado.Add(asignacion);

    public void Eliminar(AsignacionOperadorDelegado asignacion) => dbContext.AsignacionesOperadorDelegado.Remove(asignacion);
}
