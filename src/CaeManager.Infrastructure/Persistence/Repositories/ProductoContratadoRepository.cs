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

    public async Task<bool> GuardarDetectandoInboundVigenteDuplicadoAsync(
        ProductoContratado intento, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intento);
        var entradaIntento = dbContext.Entry(intento);
        if (entradaIntento.State != EntityState.Added)
            throw new InvalidOperationException("El Producto Contratado que se guarda tiene que haberse añadido antes.");

        // El intento se guarda aislado (hallazgos 2 y 3 de Codex, ronda 2). Si el contexto tuviera otro cambio pendiente,
        // un SaveChanges lo escribiría con el rol elevado, que no es para eso, y AuditoriaInterceptor consumiría la acción
        // de auditoría propia de esa entidad (Renovado, Restaurado...) aunque la base rechazara después el intento: al
        // guardarla luego, quedaría como Modificado. Así que no se llega a SaveChanges: se retira el intento, para que
        // no se cuele en otro guardado, y lo ajeno se queda tal cual, sin escribir y sin consumir nada. Entries() detecta
        // antes los cambios, así que una entidad modificada sin marcar aparece aquí como Modified.
        // Límite que queda: una entidad rastreada SIN cambios pendientes que tuviera una acción propia marcada pierde la
        // marca en este guardado, como en cualquier otro SaveChanges del contexto (el interceptor la consume siempre
        // para que no etiquete un guardado posterior). No hay registro que perder: sin cambios no se audita.
        if (dbContext.ChangeTracker.Entries().Any(e =>
                (e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                && !ReferenceEquals(e.Entity, intento)))
        {
            entradaIntento.State = EntityState.Detached;
            throw new InvalidOperationException(
                "La contratación de la línea Inbound se guarda sola: el contexto tiene cambios pendientes de otro acto.");
        }

        // Lo que ya estaba rastreado antes del guardado: lo que los interceptores añadan durante él no está aquí.
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
            // Un SaveChanges fallido no revierte el estado Added en memoria: sin retirarlos, la fila rechazada y su
            // auditoría se colarían en el siguiente guardado del mismo contexto. Se retira el intento, por referencia, y
            // lo que los interceptores añadieron durante este guardado, que es su registro de auditoría porque el intento
            // era lo único pendiente. Nada rastreado antes del guardado, salvo el intento, se toca.
            foreach (var entrada in dbContext.ChangeTracker.Entries()
                         .Where(e => e.State == EntityState.Added
                                     && (ReferenceEquals(e.Entity, intento) || !rastreadasAntes.Contains(e.Entity)))
                         .ToList())
                entrada.State = EntityState.Detached;

            return false;
        }
    }
}
