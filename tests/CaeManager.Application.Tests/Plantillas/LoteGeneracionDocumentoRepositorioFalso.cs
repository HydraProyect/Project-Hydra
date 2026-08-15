using CaeManager.Domain.Plantillas;

namespace CaeManager.Application.Tests.Plantillas;

public class LoteGeneracionDocumentoRepositorioFalso : ILoteGeneracionDocumentoRepository
{
    public List<LoteGeneracionDocumento> Lista { get; } = [];

    public void Agregar(LoteGeneracionDocumento lote) => Lista.Add(lote);

    public Task<LoteGeneracionDocumento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Lista.FirstOrDefault(l => l.Id == id));
}

public class ItemGeneracionDocumentoRepositorioFalso : IItemGeneracionDocumentoRepository
{
    public List<ItemGeneracionDocumento> Lista { get; } = [];

    public void Agregar(ItemGeneracionDocumento item) => Lista.Add(item);

    public Task<ItemGeneracionDocumento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Lista.FirstOrDefault(i => i.Id == id));
}
