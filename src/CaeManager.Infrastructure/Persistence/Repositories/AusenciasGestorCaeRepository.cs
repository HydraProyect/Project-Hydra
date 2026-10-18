using CaeManager.Application.Operaciones.Traspasos;
using CaeManager.Domain.Operaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class AusenciasGestorCaeRepository(CaeManagerDbContext db) : IAusenciasGestorCae
{
    public async Task<IReadOnlyList<AusenciaGestorCae>> ListarAbiertasAsync(CancellationToken ct) =>
        await db.Set<AusenciaGestorCae>().AsNoTracking().Where(a => a.CerradaEnUtc == null).OrderBy(a => a.Desde).ToListAsync(ct);
    public Task<AusenciaGestorCae?> ObtenerAsync(Guid id, CancellationToken ct) =>
        db.Set<AusenciaGestorCae>().FirstOrDefaultAsync(a => a.Id == id, ct);
    public void Agregar(AusenciaGestorCae ausencia) => db.Set<AusenciaGestorCae>().Add(ausencia);
}
