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
