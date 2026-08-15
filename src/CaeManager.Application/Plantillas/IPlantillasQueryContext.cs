using CaeManager.Domain.Plantillas;

namespace CaeManager.Application.Plantillas;

public interface IPlantillasQueryContext
{
    IQueryable<PlantillaDocumento> PlantillasDocumento { get; }
    IQueryable<PlantillaDocumentoVersion> PlantillasDocumentoVersion { get; }
    IQueryable<PlantillaElemento> PlantillasElemento { get; }
    IQueryable<ConocimientoDeteccionCampo> ConocimientosDeteccionCampo { get; }
}
