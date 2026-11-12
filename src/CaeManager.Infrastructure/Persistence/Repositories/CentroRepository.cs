using CaeManager.Domain.Centros;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class CentroRepository(CaeManagerDbContext dbContext) : ICentroRepository
{
    public Task<Centro?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ObtenerActualAsync(dbContext.Centros.Where(c => c.Id == id), cancellationToken);

    // Al retirar el filtro combinado se repone explícitamente el Tenant; RLS sigue aplicándose.
    public Task<Centro?> ObtenerIncluidoEliminadoActualizadoAsync(Guid id, Guid? tenantId, CancellationToken cancellationToken = default) =>
        dbContext.ObtenerActualAsync(
            dbContext.Centros.IgnoreQueryFilters().Where(c => c.Id == id && c.TenantId == tenantId), cancellationToken);

    public async Task<IReadOnlyList<Centro>> ListarActualizadosAsync(CancellationToken cancellationToken = default) =>
        await dbContext.ListarActualesAsync(dbContext.Centros, c => !c.EstaEliminado, cancellationToken);

    public Task<bool> ExisteConNombreEnClienteAsync(
        Guid clienteId, string nombre, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        dbContext.Centros.AnyAsync(
            c => c.ClienteId == clienteId && c.Nombre.ToUpper() == nombre.ToUpper() && (excluirId == null || c.Id != excluirId),
            cancellationToken);

    public void Agregar(Centro centro) => dbContext.Centros.Add(centro);
}
