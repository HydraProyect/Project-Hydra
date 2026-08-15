namespace CaeManager.Domain.Plantillas;

public interface IPlantillaDocumentoRepository
{
    void Agregar(PlantillaDocumento plantilla);

    Task<PlantillaDocumento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);
}
