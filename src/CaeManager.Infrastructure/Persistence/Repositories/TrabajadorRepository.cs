using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class TrabajadorRepository(CaeManagerDbContext dbContext) : ITrabajadorRepository
{
    public Task<Trabajador?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ObtenerActualAsync(dbContext.Trabajadores.Where(t => t.Id == id), cancellationToken);

    public Task<bool> ExisteConDniAsync(string dni, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        dbContext.Trabajadores.AnyAsync(
            t => t.Dni == dni && (excluirId == null || t.Id != excluirId),
            cancellationToken);

    public async Task<IReadOnlySet<string>> ObtenerDnisEnPapeleraAsync(
        IReadOnlyCollection<string> dnis, CancellationToken cancellationToken = default)
    {
        if (dnis.Count == 0)
            return new HashSet<string>();

        // El índice (TenantId, Dni) incluye eliminados. Al retirar el filtro combinado se repone explícitamente el
        // Tenant propietario; sin Tenant propietario resuelto no se ve ninguna fila. RLS sigue aplicándose.
        var enPapelera = await dbContext.Trabajadores.IgnoreQueryFilters().AsNoTracking()
            .Where(t => dbContext.TenantPropietarioId != null && t.TenantId == dbContext.TenantPropietarioId
                && t.EstaEliminado && t.Dni != null && dnis.Contains(t.Dni))
            .Select(t => t.Dni!)
            .ToListAsync(cancellationToken);
        return new HashSet<string>(enPapelera, StringComparer.OrdinalIgnoreCase);
    }

    public void Agregar(Trabajador trabajador) => dbContext.Trabajadores.Add(trabajador);
}
