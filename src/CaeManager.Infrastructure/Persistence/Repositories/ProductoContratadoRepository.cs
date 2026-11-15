using CaeManager.Domain.Comercial;
using CaeManager.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.Repositories;

/// <summary>
/// Sin lecturas a propósito: corre como <c>cae_app_aprovisionamiento</c>, que solo tiene INSERT sobre
/// <c>ProductosContratados</c> (D-C). El INSERT de EF no lleva RETURNING porque todas las columnas las pone el cliente.
/// </summary>
public class ProductoContratadoRepository(CaeManagerDbContext dbContext) : IProductoContratadoRepository
{
    public void Agregar(ProductoContratado producto) => dbContext.ProductosContratados.Add(producto);

    public async Task<bool> GuardarDetectandoInboundVigenteDuplicadoAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        // Se comprueba el nombre de la restricción, no cualquier 23505 (mismo patrón que EncargoAdministracionRepository).
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
                                           && pg.ConstraintName == ProductoContratadoConfiguration.IndiceInboundVigentePorTenant)
        {
            // Un SaveChanges fallido no revierte el estado Added en memoria: sin el Clear, la fila rechazada se colaría
            // en el siguiente guardado del mismo contexto.
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }
}
