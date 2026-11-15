using CaeManager.Application.Operaciones;
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
        // Solo si su origen la ampara: una Asignación de Operación externa cuya DelegacionTenant se revocó (o que
        // nació sin origen) no admite un Encargo de Administración (Codex r2 en la PR #2237, media 8).
        dbContext.AsignacionesOperacion
            .AsNoTracking()
            .AmparadasPorSuOrigen(dbContext.DelegacionesTenant, DateTime.UtcNow)
            .FirstOrDefaultAsync(
                o => o.Id == asignacionOperacionId && o.PropietarioTenantId == propietarioTenantId, cancellationToken);

    public async Task<IReadOnlyList<AsignacionOperacion>> ListarOperacionesEncargablesAsync(
        Guid propietarioTenantId, DateTime ahora, CancellationToken cancellationToken = default)
    {
        // Misma posición que ObtenerOperacionAsync: solo las del Tenant propietario pedido. El ámbito
        // y la vigencia se deciden en memoria con las reglas del dominio (las mismas que aplica
        // EncargoAdministracion.Registrar); un Tenant propietario tiene un puñado de operaciones.
        // Y solo las que su origen ampara en ese instante (Codex r2 en la PR #2237, media 8).
        var candidatas = await dbContext.AsignacionesOperacion
            .AsNoTracking()
            .AmparadasPorSuOrigen(dbContext.DelegacionesTenant, ahora)
            .Where(o => o.PropietarioTenantId == propietarioTenantId
                        && !o.EsRaiz
                        && o.OperadorTenantId != propietarioTenantId)
            .ToListAsync(cancellationToken);

        var conEncargoSinRetirar = await dbContext.EncargosAdministracion
            .AsNoTracking()
            .Where(e => e.PropietarioTenantId == propietarioTenantId && e.RetiradoEnUtc == null)
            .Select(e => e.AsignacionOperacionId)
            .ToListAsync(cancellationToken);

        return candidatas
            .Where(o => !o.EsOperacionInterna
                        && o.Ambito.EsUniversal
                        && o.EstaVigenteEn(ahora)
                        && !conEncargoSinRetirar.Contains(o.Id))
            .OrderBy(o => o.Id)
            .ToList();
    }

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
