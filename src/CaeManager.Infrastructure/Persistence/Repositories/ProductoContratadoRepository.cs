using CaeManager.Domain.Auditoria;
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
        // Lo que ya estaba rastreado antes del intento: lo que AuditoriaInterceptor añada durante el guardado no está aquí.
        var rastreadasAntes = dbContext.ChangeTracker.Entries()
            .Select(e => e.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        // Se comprueba el nombre de la restricción, no cualquier 23505 (mismo patrón que EncargoAdministracionRepository).
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
                                           && pg.ConstraintName == ProductoContratadoConfiguration.IndiceInboundVigentePorTenant)
        {
            // Un SaveChanges fallido no revierte el estado Added en memoria: sin retirarlas, la fila rechazada y los
            // registros de auditoría de este intento se colarían en el siguiente guardado del mismo contexto. Solo se
            // retiran esas (como CatalogoTrabajadoresEmpresaRepository): los cambios pendientes de otro acto siguen
            // rastreados, y su auditoría la vuelve a generar el interceptor cuando se guarden. Este repositorio es el
            // único que añade Productos Contratados, así que todo Producto Contratado añadido es de este intento.
            foreach (var entrada in dbContext.ChangeTracker.Entries()
                         .Where(e => e.State == EntityState.Added
                                     && (e.Entity is ProductoContratado
                                         || (e.Entity is RegistroAuditoria && !rastreadasAntes.Contains(e.Entity))))
                         .ToList())
                entrada.State = EntityState.Detached;

            return false;
        }
    }
}
