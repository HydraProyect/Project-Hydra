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
    public List<DocumentoGenerado> ListaDocumentosGenerados { get; } = [];
    public List<LoteGeneracionDocumento> ListaLotesGeneracionDocumento { get; } = [];
    public List<ItemGeneracionDocumento> ListaItemsGeneracionDocumento { get; } = [];

    public IQueryable<PlantillaDocumento> PlantillasDocumento => new TestAsyncQueryable<PlantillaDocumento>(ListaPlantillasDocumento.AsQueryable());
    public IQueryable<PlantillaDocumentoVersion> PlantillasDocumentoVersion => new TestAsyncQueryable<PlantillaDocumentoVersion>(ListaPlantillasDocumentoVersion.AsQueryable());
    public IQueryable<PlantillaElemento> PlantillasElemento => new TestAsyncQueryable<PlantillaElemento>(ListaPlantillasElemento.AsQueryable());
    public IQueryable<ConocimientoDeteccionCampo> ConocimientosDeteccionCampo => new TestAsyncQueryable<ConocimientoDeteccionCampo>(ListaConocimientosDeteccionCampo.AsQueryable());
    public IQueryable<DocumentoGenerado> DocumentosGenerados => new TestAsyncQueryable<DocumentoGenerado>(ListaDocumentosGenerados.AsQueryable());
    public IQueryable<LoteGeneracionDocumento> LotesGeneracionDocumento => new TestAsyncQueryable<LoteGeneracionDocumento>(ListaLotesGeneracionDocumento.AsQueryable());
    /// <summary>
    /// Se llama justo antes de cada lectura de <see cref="ItemsGeneracionDocumento"/> con su número de orden (1, 2...),
    /// para simular otro circuito que cambia los ítems entre dos lecturas del mismo handler (intercalado TOCTOU).
    /// </summary>
    public Action<int>? AntesDeLeerItems { get; set; }

    private int _lecturasDeItems;

    public IQueryable<ItemGeneracionDocumento> ItemsGeneracionDocumento
    {
        get
        {
            AntesDeLeerItems?.Invoke(++_lecturasDeItems);
            return new TestAsyncQueryable<ItemGeneracionDocumento>(ListaItemsGeneracionDocumento.AsQueryable());
        }
    }
}
