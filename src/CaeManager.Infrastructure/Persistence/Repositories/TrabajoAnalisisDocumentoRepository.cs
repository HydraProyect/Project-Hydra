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

    public async Task<TrabajoAnalisisDocumento?> ReclamarSiguientePendienteAsync(CancellationToken cancellationToken = default)
    {
        var ahoraUtc = DateTime.UtcNow;

        // Transacción corta propia: el lock de fila de FOR UPDATE SKIP LOCKED
        // solo protege mientras la transacción que lo tomó sigue abierta. Se
        // libera al hacer commit más abajo, justo después de confirmar el
        // cambio a "Procesando" — nunca se mantiene abierta durante el
        // análisis en sí.
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
