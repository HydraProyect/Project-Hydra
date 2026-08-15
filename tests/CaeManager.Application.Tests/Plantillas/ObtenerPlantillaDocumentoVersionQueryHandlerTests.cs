using CaeManager.Application.Plantillas.Queries.ObtenerPlantillaDocumentoVersion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

public class ObtenerPlantillaDocumentoVersionQueryHandlerTests
{
    [Fact]
    public async Task Devuelve_metadata_de_la_version_y_sus_elementos()
    {
        var contexto = new PlantillasQueryContextFalso();
        var documento = new PlantillaDocumento(
            OrigenPlantilla.Externa, "Ficha de riesgos", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfConCampos);
        var version = new PlantillaDocumentoVersion(documento.Id, 1, "url.pdf", new string('a', 64));
        var elemento = new PlantillaElemento(version.Id, TipoElementoPlantilla.Texto, 1, 10, 20, 100, 15, "CIF",
            FuenteDatoPlantilla.EmpresaCif);
        contexto.ListaPlantillasDocumento.Add(documento);
        contexto.ListaPlantillasDocumentoVersion.Add(version);
        contexto.ListaPlantillasElemento.Add(elemento);
        var handler = new ObtenerPlantillaDocumentoVersionQueryHandler(contexto);

        var resultado = await handler.Handle(new ObtenerPlantillaDocumentoVersionQuery(version.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.NombrePlantilla.Should().Be("Ficha de riesgos");
        resultado.Valor.ArchivoOriginalUrl.Should().Be("url.pdf");
        resultado.Valor.Elementos.Should().ContainSingle(e => e.EtiquetaVisible == "CIF");
    }

    [Fact]
    public async Task Devuelve_fallo_si_la_version_no_existe()
    {
        var handler = new ObtenerPlantillaDocumentoVersionQueryHandler(new PlantillasQueryContextFalso());

        var resultado = await handler.Handle(new ObtenerPlantillaDocumentoVersionQuery(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.VersionNoEncontrada");
    }
}
