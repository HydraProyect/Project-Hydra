using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ConcesionCapacidadTenantRepository(CaeManagerDbContext dbContext) : IConcesionCapacidadTenantRepository
{
    public Task<ConcesionCapacidadTenant?> ObtenerVigenteAsync(
        Guid usuarioId, CapacidadTenant capacidad, CancellationToken cancellationToken) =>
        dbContext.ConcesionesCapacidadTenant
            .Where(c => c.UsuarioId == usuarioId && c.Capacidad == capacidad && c.RevocadaEnUtc == null)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<bool> TieneVigenteAsync(Guid usuarioId, CapacidadTenant capacidad, CancellationToken cancellationToken) =>
        dbContext.ConcesionesCapacidadTenant
            .AsNoTracking()
            .AnyAsync(c => c.UsuarioId == usuarioId && c.Capacidad == capacidad && c.RevocadaEnUtc == null,
                cancellationToken);

    public void Agregar(ConcesionCapacidadTenant concesion) => dbContext.ConcesionesCapacidadTenant.Add(concesion);

    public async Task<bool> GuardarConcesionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        // Solo el índice único parcial de una concesión vigente por cuenta y capacidad, no cualquier 23505 (mismo
        // criterio que ContactoSeguridadTenantRepository.GuardarPropuestaAsync).
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: ConcesionCapacidadTenantConfiguration.IndiceUnaVigentePorUsuarioYCapacidad
        })
        {
            // La base rechazó el lote entero, auditoría incluida: no puede reintentarse en un guardado posterior.
            ConcurrenciaContexto.DescartarLoteFallido(dbContext);
            return false;
        }
    }
}
