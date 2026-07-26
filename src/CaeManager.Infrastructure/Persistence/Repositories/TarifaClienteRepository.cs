using CaeManager.Domain.Facturacion;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class TarifaClienteRepository(CaeManagerDbContext dbContext) : ITarifaClienteRepository
{
    public Task<TarifaCliente?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.TarifasCliente.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public Task<List<TarifaCliente>> ObtenerPorClienteAsync(Guid clienteId, CancellationToken cancellationToken = default) =>
        dbContext.TarifasCliente
            .Where(t => t.ClienteId == clienteId)
            .OrderBy(t => t.Concepto)
            .ToListAsync(cancellationToken);

    public Task<bool> ExisteParaConceptoAsync(Guid clienteId, ConceptoFacturable concepto, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        dbContext.TarifasCliente
            .AnyAsync(t => t.ClienteId == clienteId
                        && t.Concepto == concepto
                        && (excluirId == null || t.Id != excluirId),
                      cancellationToken);

    public void Agregar(TarifaCliente tarifa) => dbContext.TarifasCliente.Add(tarifa);

    public void Eliminar(TarifaCliente tarifa) => dbContext.TarifasCliente.Remove(tarifa);
}
