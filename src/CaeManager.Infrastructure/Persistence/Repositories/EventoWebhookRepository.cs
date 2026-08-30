using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class EventoWebhookRepository(CaeManagerDbContext dbContext) : IEventoWebhookRepository
{
    public Task<EventoWebhook?> ReclamarSiguientePendienteAsync(
        ProveedorIntegracion proveedor, CancellationToken cancellationToken = default)
    {
        var ahoraUtc = DateTime.UtcNow;
        var proveedorValor = (int)proveedor;

        // Ver TrabajoAnalisisDocumentoRepository.ReclamarSiguientePendienteAsync:
        // la transacción explícita tiene que ir envuelta en la estrategia de
        // ejecución del contexto — NpgsqlRetryingExecutionStrategy (activa vía
        // EnableRetryOnFailure) no admite una transacción que el propio
        // código abrió sin pasar por CreateExecutionStrategy().
        var estrategia = dbContext.Database.CreateExecutionStrategy();

        return estrategia.ExecuteAsync(async () =>
        {
            // Transacción corta — el lock de fila de FOR UPDATE SKIP LOCKED
            // solo protege mientras esta transacción sigue abierta, y se
            // libera al hacer commit más abajo, justo tras confirmar el
            // cambio a "Procesando". "FOR UPDATE OF eventos" (no de toda la
            // consulta): ConexionesIntegracion solo participa como filtro,
            // bloquear esa fila también no aportaría nada y competiría
            // innecesariamente con quien esté editando la conexión.
            await using var transaccion = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var evento = await dbContext.EventosWebhook
                .FromSqlInterpolated($"""
                    SELECT eventos.* FROM "EventosWebhook" eventos
                    JOIN "ConexionesIntegracion" conexion ON eventos."ConexionIntegracionId" = conexion."Id"
                    WHERE eventos."Estado" = 'Pendiente'
                      AND conexion."Proveedor" = {proveedorValor}
                      AND (eventos."SiguienteIntentoEnUtc" IS NULL OR eventos."SiguienteIntentoEnUtc" <= {ahoraUtc})
                    ORDER BY eventos."FechaRecepcionUtc"
                    LIMIT 1
                    FOR UPDATE OF eventos SKIP LOCKED
                    """)
                .SingleOrDefaultAsync(cancellationToken);

            if (evento is null)
            {
                await transaccion.CommitAsync(cancellationToken);
                return null;
            }

            evento.MarcarEnProceso();
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaccion.CommitAsync(cancellationToken);

            return evento;
        });
    }

    public async Task<IReadOnlyList<EventoWebhook>> ObtenerEstancadosAsync(
        ProveedorIntegracion proveedor, TimeSpan umbral, CancellationToken cancellationToken = default)
    {
        var limite = DateTime.UtcNow - umbral;
        return await (from evento in dbContext.EventosWebhook
                      join conexion in dbContext.ConexionesIntegracion on evento.ConexionIntegracionId equals conexion.Id
                      where evento.Estado == EstadoEventoWebhook.Procesando
                            && evento.IniciadoEnUtc != null && evento.IniciadoEnUtc < limite
                            && conexion.Proveedor == proveedor
                      select evento)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EventoWebhook>> ObtenerParaRedactarAsync(
        DateTime limiteUtc, int maximo, CancellationToken cancellationToken = default) =>
        await dbContext.EventosWebhook
            .Where(e => !e.PayloadRedactado
                        && (e.Estado == EstadoEventoWebhook.Completado || e.Estado == EstadoEventoWebhook.DescartadoDefinitivo)
                        && e.FechaRecepcionUtc < limiteUtc)
            .OrderBy(e => e.FechaRecepcionUtc)
            .Take(maximo)
            .ToListAsync(cancellationToken);

    public void Agregar(EventoWebhook evento) => dbContext.EventosWebhook.Add(evento);
}
