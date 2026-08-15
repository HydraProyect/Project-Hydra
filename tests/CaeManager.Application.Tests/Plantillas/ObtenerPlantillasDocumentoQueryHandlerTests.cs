using CaeManager.Application.Plantillas.Queries.ObtenerPlantillasDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

public class ObtenerPlantillasDocumentoQueryHandlerTests
{
    [Fact]
    public async Task Devuelve_la_ultima_version_de_cada_plantilla()
    {
        var contexto = new PlantillasQueryContextFalso();
        var documento = new PlantillaDocumento(
            OrigenPlantilla.Externa, "Ficha de riesgos", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfConCampos, Guid.NewGuid());
        var v1 = new PlantillaDocumentoVersion(documento.Id, 1, "url1.pdf", new string('a', 64));
        var v2 = new PlantillaDocumentoVersion(documento.Id, 2, "url2.pdf", new string('b', 64));
        contexto.ListaPlantillasDocumento.Add(documento);
        contexto.ListaPlantillasDocumentoVersion.AddRange([v1, v2]);
        var handler = new ObtenerPlantillasDocumentoQueryHandler(contexto);

        var resultado = await handler.Handle(new ObtenerPlantillasDocumentoQuery(), CancellationToken.None);

        var fila = resultado.Should().ContainSingle().Subject;
        fila.UltimaVersionId.Should().Be(v2.Id);
    }

    [Fact]
    public async Task No_incluye_plantillas_talveg_propio()
    {
        var contexto = new PlantillasQueryContextFalso();
        var externa = new PlantillaDocumento(
            OrigenPlantilla.Externa, "Externa", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfConCampos, Guid.NewGuid());
        var propia = new PlantillaDocumento(
            OrigenPlantilla.TalvegPropio, "Propia", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.HtmlCss, Guid.NewGuid(), codigoFormato: "F-01");
        contexto.ListaPlantillasDocumento.AddRange([externa, propia]);
        contexto.ListaPlantillasDocumentoVersion.Add(new PlantillaDocumentoVersion(externa.Id, 1, "url.pdf", new string('a', 64)));
        var handler = new ObtenerPlantillasDocumentoQueryHandler(contexto);

        var resultado = await handler.Handle(new ObtenerPlantillasDocumentoQuery(), CancellationToken.None);

        resultado.Should().ContainSingle(p => p.Nombre == "Externa");
    }
}
