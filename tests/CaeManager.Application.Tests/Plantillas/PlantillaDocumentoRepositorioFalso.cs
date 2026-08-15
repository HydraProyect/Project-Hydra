using CaeManager.Domain.Plantillas;

namespace CaeManager.Application.Tests.Plantillas;

public class PlantillaDocumentoRepositorioFalso : IPlantillaDocumentoRepository
{
    public List<PlantillaDocumento> Lista { get; } = [];

    public void Agregar(PlantillaDocumento plantilla) => Lista.Add(plantilla);

    public Task<PlantillaDocumento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Lista.FirstOrDefault(p => p.Id == id));
}

public class PlantillaDocumentoVersionRepositorioFalso : IPlantillaDocumentoVersionRepository
{
    public List<PlantillaDocumentoVersion> Lista { get; } = [];

    public void Agregar(PlantillaDocumentoVersion version) => Lista.Add(version);

    public Task<PlantillaDocumentoVersion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Lista.FirstOrDefault(v => v.Id == id));

    public Task<IReadOnlyList<PlantillaDocumentoVersion>> ObtenerPorPlantillaAsync(
        Guid plantillaDocumentoId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PlantillaDocumentoVersion>>(
            Lista.Where(v => v.PlantillaDocumentoId == plantillaDocumentoId).ToList());
}
