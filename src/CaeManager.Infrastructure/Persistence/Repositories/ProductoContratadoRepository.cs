using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Comercial;
using CaeManager.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.Repositories;

/// <summary>
/// Corre como <c>cae_app_aprovisionamiento</c>, que sobre <c>ProductosContratados</c> solo inserta, lee las columnas del
/// WHERE de la baja y actualiza <c>Estado</c> y <c>VigenteHastaUtc</c> (D-C y opción B, 2026-11-15). Por eso nunca
/// materializa una fila: el INSERT de EF no lleva RETURNING porque todas las columnas las pone el cliente, y la baja va con
/// <c>ExecuteUpdateAsync</c> y deja a mano su registro de auditoría, que <c>AuditoriaInterceptor</c> no ve.
/// <para>
/// Por la misma razón la baja no renueva <c>Version</c>, que <c>ConcurrenciaOptimistaInterceptor</c> renovaría en un
/// SaveChanges: el rol elevado no puede escribir esa columna. Hoy no hay otro escritor de la fila (solo este rol la
/// actualiza, y solo de Vigente a Finalizado, que es final), así que el token no protege nada; si algún día otro flujo
/// modifica un Producto Contratado con concurrencia optimista, la baja tendrá que renovarlo también.
/// </para>
/// </summary>
public class ProductoContratadoRepository(CaeManagerDbContext dbContext, IActorAuditoria actorAuditoria)
    : IProductoContratadoRepository
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

    public async Task<FinalizacionInbound> FinalizarInboundVigenteAsync(CancellationToken cancellationToken = default)
    {
        // Mismo aislamiento que el guardado del alta: el registro de auditoría se guarda con SaveChanges bajo el rol
        // elevado, que no debe arrastrar escrituras de otro acto.
        if (dbContext.ChangeTracker.Entries().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException(
                "La baja de la línea Inbound se escribe sola: el contexto tiene cambios pendientes de otro acto.");

        // Solo el Id, y el WHERE con las columnas que el rol elevado puede leer. El filtro global de la entidad añade
        // TenantId = Tenant activo y NOT EstaEliminado; la política RESTRICTIVE de SELECT repite la línea y el Tenant del
        // contexto firmado. Como mucho una fila: el índice único parcial.
        var id = await dbContext.ProductosContratados
            .Where(p => p.Linea == LineaProductoContratado.Inbound && p.Estado == EstadoProductoContratado.Vigente)
            .Select(p => (Guid?)p.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (id is not { } productoId)
            return new FinalizacionInbound(ResultadoFinalizacionInbound.SinInboundVigente, null);

        // El fin es el now() de la transacción (el inicio de la transacción en PostgreSQL): el mismo que usa el WITH CHECK
        // de las políticas, y anterior a cualquier clock_timestamp() lector que vea el COMMIT (gate DN-7). Dentro de la
        // transacción del comando, la corrección inserta la nueva con ese mismo instante como inicio.
        var vigenteHastaUtc = DateTime.SpecifyKind(
            await dbContext.Database.SqlQuery<DateTime>($"SELECT now() AS \"Value\"").SingleAsync(cancellationToken),
            DateTimeKind.Utc);

        int filas;
        try
        {
            // El WHERE repite el estado de partida: si otra transacción la finalizó entre la lectura y aquí, el UPDATE
            // espera su bloqueo de fila, reevalúa la condición y no toca nada.
            filas = await dbContext.ProductosContratados
                .Where(p => p.Id == productoId
                            && p.Linea == LineaProductoContratado.Inbound
                            && p.Estado == EstadoProductoContratado.Vigente)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Estado, EstadoProductoContratado.Finalizado)
                    .SetProperty(p => p.VigenteHastaUtc, vigenteHastaUtc), cancellationToken);
        }
        // Fin antes del inicio: la vigencia aún no había empezado. ExecuteUpdate no pasa por SaveChanges, así que la
        // PostgresException suele llegar sin envolver; se acepta también envuelta para no depender de la versión de EF.
        catch (Exception ex) when ((ex as PostgresException ?? ex.InnerException as PostgresException) is
        { SqlState: PostgresErrorCodes.CheckViolation } pg
                                   && pg.ConstraintName == ProductoContratadoConfiguration.RestriccionEstadoYVigencia)
        {
            return new FinalizacionInbound(ResultadoFinalizacionInbound.VigenciaAunNoIniciada, null);
        }

        if (filas == 0)
            return new FinalizacionInbound(ResultadoFinalizacionInbound.SinInboundVigente, null);

        // ExecuteUpdateAsync no pasa por AuditoriaInterceptor: el registro se escribe a mano, con la misma forma que el
        // «Modificado» que dejaría el interceptor (solo las propiedades cambiadas, por nombre) y el mismo actor.
        var actor = await actorAuditoria.ObtenerAsync();
        dbContext.RegistrosAuditoria.Add(new RegistroAuditoria(
            nameof(ProductoContratado), productoId, "Modificado",
            datosAntes: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [nameof(ProductoContratado.Estado)] = EstadoProductoContratado.Vigente,
                [nameof(ProductoContratado.VigenteHastaUtc)] = null,
            }),
            datosDespues: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [nameof(ProductoContratado.Estado)] = EstadoProductoContratado.Finalizado,
                [nameof(ProductoContratado.VigenteHastaUtc)] = vigenteHastaUtc,
            }),
            usuarioId: actor.UsuarioSimuladoId ?? actor.ActorRealUsuarioId,
            tipoActor: (TipoActorAuditoria)actor.ResolverTipoActor(),
            actorRealUsuarioId: actor.ActorRealUsuarioId,
            viaAcceso: (TipoViaAccesoAuditoria)actor.Via,
            viaAccesoId: actor.ViaAccesoId));
        await dbContext.SaveChangesAsync(cancellationToken);

        return new FinalizacionInbound(ResultadoFinalizacionInbound.Finalizada, productoId, vigenteHastaUtc);
    }
}
