using CaeManager.Domain.DocumentosIa;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class TrabajoAnalisisDocumentoRepository(CaeManagerDbContext dbContext) : ITrabajoAnalisisDocumentoRepository
{
    public void Agregar(TrabajoAnalisisDocumento trabajo) => dbContext.TrabajosAnalisisDocumento.Add(trabajo);

    public Task<TrabajoAnalisisDocumento?> ObtenerSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
        dbContext.TrabajosAnalisisDocumento
            .Where(t => t.Estado == EstadoTrabajoAnalisisDocumento.Pendiente)
            .OrderBy(t => t.CreadoEnUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<TrabajoAnalisisDocumento?> ReclamarSiguientePendienteAsync(CancellationToken cancellationToken = default)
    {
        var ahoraUtc = DateTime.UtcNow;

        // La transacción explícita hay que envolverla en la estrategia de
        // ejecución del contexto (CreateExecutionStrategy), no abrirla
        // directamente con BeginTransactionAsync — con EnableRetryOnFailure
        // activo (ver ConfiguracionDeContexto), NpgsqlRetryingExecutionStrategy
        // lanza InvalidOperationException en cuanto detecta una transacción
        // iniciada por el propio código: no sabe reintentar de forma segura
        // una transacción que no controla ella misma. Sin este envoltorio, la
        // excepción no llegaba a los tests de integración (su DbContextOptions
        // no activa reintentos) pero sí en la app real — auditoría de colas,
        // 2026-08-30, hallazgo E2E: el reclamo fallaba en CADA sondeo y el
        // trabajo se quedaba "Pendiente" para siempre.
        var estrategia = dbContext.Database.CreateExecutionStrategy();

        return estrategia.ExecuteAsync(async () =>
        {
            // Transacción corta: el lock de fila de FOR UPDATE SKIP LOCKED
            // solo protege mientras la transacción que lo tomó sigue abierta.
            // Se libera al hacer commit más abajo, justo después de
            // confirmar el cambio a "Procesando" — nunca se mantiene abierta
            // durante el análisis en sí.
            await using var transaccion = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var trabajo = await dbContext.TrabajosAnalisisDocumento
                .FromSqlInterpolated($"""
                    SELECT * FROM "TrabajosAnalisisDocumento"
                    WHERE "Estado" = 'Pendiente'
                      AND ("SiguienteIntentoEnUtc" IS NULL OR "SiguienteIntentoEnUtc" <= {ahoraUtc})
                    ORDER BY "CreadoEnUtc"
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                    """)
                .SingleOrDefaultAsync(cancellationToken);

            if (trabajo is null)
            {
                await transaccion.CommitAsync(cancellationToken);
                return null;
            }

            trabajo.MarcarEnProceso();
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaccion.CommitAsync(cancellationToken);

            return trabajo;
        });
    }

    public async Task<IReadOnlyList<TrabajoAnalisisDocumento>> ObtenerEstancadosAsync(
        TimeSpan umbral, CancellationToken cancellationToken = default)
    {
        var limite = DateTime.UtcNow - umbral;
        return await dbContext.TrabajosAnalisisDocumento
            .Where(t => t.Estado == EstadoTrabajoAnalisisDocumento.Procesando && t.IniciadoEnUtc != null && t.IniciadoEnUtc < limite)
            .ToListAsync(cancellationToken);
    }

    public Task<int> ContarActivosAsync(CancellationToken cancellationToken = default) =>
        dbContext.TrabajosAnalisisDocumento
            .Where(t => t.Estado == EstadoTrabajoAnalisisDocumento.Pendiente || t.Estado == EstadoTrabajoAnalisisDocumento.Procesando)
            .CountAsync(cancellationToken);
}
