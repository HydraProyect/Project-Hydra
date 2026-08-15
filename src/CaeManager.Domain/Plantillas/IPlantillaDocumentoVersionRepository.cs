namespace CaeManager.Domain.Plantillas;

public interface IPlantillaDocumentoVersionRepository
{
    void Agregar(PlantillaDocumentoVersion version);

    /// <summary>Incluye <see cref="PlantillaDocumentoVersion.Elementos"/> — es lo que necesita el editor visual y <c>EstablecerElementos</c>.</summary>
    Task<PlantillaDocumentoVersion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlantillaDocumentoVersion>> ObtenerPorPlantillaAsync(
        Guid plantillaDocumentoId, CancellationToken cancellationToken = default);
}
