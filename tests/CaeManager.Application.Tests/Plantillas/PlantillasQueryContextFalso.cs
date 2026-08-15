using CaeManager.Application.Plantillas;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Plantillas;

namespace CaeManager.Application.Tests.Plantillas;

public class PlantillasQueryContextFalso : IPlantillasQueryContext
{
    public List<PlantillaDocumento> ListaPlantillasDocumento { get; } = [];
    public List<PlantillaDocumentoVersion> ListaPlantillasDocumentoVersion { get; } = [];
    public List<PlantillaElemento> ListaPlantillasElemento { get; } = [];
    public List<ConocimientoDeteccionCampo> ListaConocimientosDeteccionCampo { get; } = [];

    public IQueryable<PlantillaDocumento> PlantillasDocumento => new TestAsyncQueryable<PlantillaDocumento>(ListaPlantillasDocumento.AsQueryable());
    public IQueryable<PlantillaDocumentoVersion> PlantillasDocumentoVersion => new TestAsyncQueryable<PlantillaDocumentoVersion>(ListaPlantillasDocumentoVersion.AsQueryable());
    public IQueryable<PlantillaElemento> PlantillasElemento => new TestAsyncQueryable<PlantillaElemento>(ListaPlantillasElemento.AsQueryable());
    public IQueryable<ConocimientoDeteccionCampo> ConocimientosDeteccionCampo => new TestAsyncQueryable<ConocimientoDeteccionCampo>(ListaConocimientosDeteccionCampo.AsQueryable());
}
