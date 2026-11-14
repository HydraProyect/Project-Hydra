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

    /// <summary>Cuántas consultas se han abierto sobre cualquiera de las colecciones de este contexto.</summary>
    public int Lecturas { get; private set; }

    private TestAsyncQueryable<T> Leer<T>(List<T> lista)
    {
        Lecturas++;
        return new TestAsyncQueryable<T>(lista.AsQueryable());
    }

    public IQueryable<PlantillaDocumento> PlantillasDocumento => Leer(ListaPlantillasDocumento);
    public IQueryable<PlantillaDocumentoVersion> PlantillasDocumentoVersion => Leer(ListaPlantillasDocumentoVersion);
    public IQueryable<PlantillaElemento> PlantillasElemento => Leer(ListaPlantillasElemento);
    public IQueryable<ConocimientoDeteccionCampo> ConocimientosDeteccionCampo => Leer(ListaConocimientosDeteccionCampo);
    public IQueryable<DocumentoGenerado> DocumentosGenerados => Leer(ListaDocumentosGenerados);
    public IQueryable<LoteGeneracionDocumento> LotesGeneracionDocumento => Leer(ListaLotesGeneracionDocumento);
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
            return Leer(ListaItemsGeneracionDocumento);
        }
    }
}
