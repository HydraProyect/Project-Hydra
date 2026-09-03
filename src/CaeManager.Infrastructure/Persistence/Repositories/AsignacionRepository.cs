using CaeManager.Domain.Asignaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class AsignacionRepository(CaeManagerDbContext dbContext) : IAsignacionRepository
{
    public Task<Asignacion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Asignaciones.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<bool> ExisteActivaAsync(Guid trabajadorId, Guid centroId, CancellationToken cancellationToken = default) =>
        dbContext.Asignaciones.AnyAsync(
            a => a.TrabajadorId == trabajadorId && a.CentroId == centroId && a.FechaBaja == null,
            cancellationToken);

    public Task<bool> ExisteSolapeAsync(
        Guid trabajadorId, Guid centroId, DateOnly fechaAlta, DateOnly? fechaBaja, CancellationToken cancellationToken = default)
    {
        // Misma semántica que Asignacion.SeSolapaCon, en forma traducible a
        // SQL: no se puede invocar el método de dominio dentro del árbol de
        // expresión de EF, así que la condición se repite aquí — igual que
        // ExisteActivaAsync repite "FechaBaja == null" en vez de llamar a
        // EstaActiva.
        var bajaEfectiva = fechaBaja ?? DateOnly.MaxValue;

        // Un rango vacío (FechaAlta == FechaBaja, ver Asignacion.SeSolapaCon)
        // no se solapa con nada — ni el candidato ni una fila existente.
        if (fechaAlta == fechaBaja) return Task.FromResult(false);

        return dbContext.Asignaciones.AnyAsync(
            a => a.TrabajadorId == trabajadorId && a.CentroId == centroId
                && a.FechaBaja != a.FechaAlta
                && a.FechaAlta < bajaEfectiva
                && fechaAlta < (a.FechaBaja ?? DateOnly.MaxValue),
            cancellationToken);
    }

    public async Task<IReadOnlyList<Asignacion>> ObtenerActivasPorCentroAsync(
        Guid centroId, CancellationToken cancellationToken = default) =>
        await dbContext.Asignaciones
            .Where(a => a.CentroId == centroId && a.FechaBaja == null)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Asignacion>> ObtenerActivasPorTrabajadorAsync(
        Guid trabajadorId, CancellationToken cancellationToken = default) =>
        await dbContext.Asignaciones
            .Where(a => a.TrabajadorId == trabajadorId && a.FechaBaja == null)
            .ToListAsync(cancellationToken);

    public void Agregar(Asignacion asignacion) => dbContext.Asignaciones.Add(asignacion);
}
