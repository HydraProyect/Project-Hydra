using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class EncargoAdministracionRepository(CaeManagerDbContext dbContext) : IEncargoAdministracionRepository
{
    public Task<EncargoAdministracion?> ObtenerPorIdAsync(
        Guid id, Guid propietarioTenantId, CancellationToken cancellationToken = default) =>
        dbContext.EncargosAdministracion
            .FirstOrDefaultAsync(e => e.Id == id && e.PropietarioTenantId == propietarioTenantId, cancellationToken);

    public Task<AsignacionOperacion?> ObtenerOperacionAsync(
        Guid asignacionOperacionId, Guid propietarioTenantId, CancellationToken cancellationToken = default) =>
        dbContext.AsignacionesOperacion
            .AsNoTracking()
            .FirstOrDefaultAsync(
                o => o.Id == asignacionOperacionId && o.PropietarioTenantId == propietarioTenantId, cancellationToken);

    public Task<bool> ExisteSinRetirarAsync(Guid asignacionOperacionId, CancellationToken cancellationToken = default) =>
        dbContext.EncargosAdministracion.AnyAsync(
            e => e.AsignacionOperacionId == asignacionOperacionId && e.RetiradoEnUtc == null, cancellationToken);

    public void Agregar(EncargoAdministracion encargo) => dbContext.EncargosAdministracion.Add(encargo);

    public async Task<bool> GuardarDetectandoCarreraAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }
        // Se comprueba el nombre de la restricción, no cualquier 23505 (mismo patrón que
        // CatalogoIncorporacionCartera).
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" } pg
                                           && pg.ConstraintName == EncargoAdministracionConfiguration.IndiceVigentePorOperacion)
        {
            // Un SaveChanges fallido no revierte el estado Added en memoria: sin el Clear,
            // el encargo que perdió la carrera se colaría en el siguiente guardado del contexto.
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }
}
