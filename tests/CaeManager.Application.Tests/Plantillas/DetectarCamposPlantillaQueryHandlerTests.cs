using CaeManager.Application.Common;
using CaeManager.Application.DocumentosIa;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Application.Plantillas.Queries.DetectarCamposPlantilla;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

public class DetectarCamposPlantillaQueryHandlerTests
{
    private static PlantillasQueryContextFalso ContextoConConocimientoBasico()
    {
        var contexto = new PlantillasQueryContextFalso();
        contexto.ListaConocimientosDeteccionCampo.AddRange(
        [
            new ConocimientoDeteccionCampo("cif", FuenteDatoPlantilla.EmpresaCif, 100),
            new ConocimientoDeteccionCampo("nombre y apellidos", FuenteDatoPlantilla.TrabajadorNombreCompleto, 100),
        ]);
        return contexto;
    }

    [Fact]
    public async Task PdfConCampos_propone_fuente_de_dato_para_coincidencia_exacta()
    {
        var extractor = new ExtractorAcroFormFalso(
        [
            new CampoAcroFormDetectado("CIF", 1, 10, 20, 100, 15)
        ]);
        var handler = new DetectarCamposPlantillaQueryHandler(
            extractor, new RouterQueLanzaSiSeInvoca(), ContextoConConocimientoBasico(),
            Options.Create(new DeteccionPreviaDocumentoOptions { Activa = false }));

        var resultado = await handler.Handle(
            new DetectarCamposPlantillaQuery([1, 2, 3], FormatoOrigenPlantilla.PdfConCampos), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var candidato = resultado.Valor.Should().ContainSingle().Subject;
        candidato.FuenteDatoSugerida.Should().Be(FuenteDatoPlantilla.EmpresaCif);
        candidato.Confianza.Should().Be(100);
        candidato.NombreCampoAcroForm.Should().Be("CIF");
        candidato.Tipo.Should().Be(TipoElementoPlantilla.Texto);
        candidato.Pagina.Should().Be(1);
    }

    [Fact]
    public async Task PdfConCampos_con_nombre_de_campo_pegado_encuentra_coincidencia_parcial()
    {
        var extractor = new ExtractorAcroFormFalso(
        [
            new CampoAcroFormDetectado("txtCIF", 1, 0, 0, 50, 10)
        ]);
        var handler = new DetectarCamposPlantillaQueryHandler(
            extractor, new RouterQueLanzaSiSeInvoca(), ContextoConConocimientoBasico(),
            Options.Create(new DeteccionPreviaDocumentoOptions { Activa = false }));

        var resultado = await handler.Handle(
            new DetectarCamposPlantillaQuery([1], FormatoOrigenPlantilla.PdfConCampos), CancellationToken.None);

        var candidato = resultado.Valor.Should().ContainSingle().Subject;
        candidato.FuenteDatoSugerida.Should().Be(FuenteDatoPlantilla.EmpresaCif);
        candidato.Confianza.Should().Be(60);
    }

    [Fact]
    public async Task PdfConCampos_sin_coincidencia_no_sugiere_fuente()
    {
        var extractor = new ExtractorAcroFormFalso(
        [
            new CampoAcroFormDetectado("CampoRarísimo", 1, 0, 0, 50, 10)
        ]);
        var handler = new DetectarCamposPlantillaQueryHandler(
            extractor, new RouterQueLanzaSiSeInvoca(), ContextoConConocimientoBasico(),
            Options.Create(new DeteccionPreviaDocumentoOptions { Activa = false }));

        var resultado = await handler.Handle(
            new DetectarCamposPlantillaQuery([1], FormatoOrigenPlantilla.PdfConCampos), CancellationToken.None);

        var candidato = resultado.Valor.Should().ContainSingle().Subject;
        candidato.FuenteDatoSugerida.Should().BeNull();
        candidato.Confianza.Should().Be(0);
    }

    [Fact]
    public async Task PdfVisual_con_la_opcion_desactivada_no_llama_al_router_y_devuelve_vacio()
    {
        var handler = new DetectarCamposPlantillaQueryHandler(
            new ExtractorAcroFormFalso([]), new RouterQueLanzaSiSeInvoca(), ContextoConConocimientoBasico(),
            Options.Create(new DeteccionPreviaDocumentoOptions { Activa = false }));

        var resultado = await handler.Handle(
            new DetectarCamposPlantillaQuery([1, 2, 3], FormatoOrigenPlantilla.PdfVisual), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().BeEmpty();
    }

    [Fact]
    public async Task PdfVisual_con_la_opcion_activada_propone_candidatos_desde_el_texto_extraido()
    {
        var router = new RouterFalso(Result.Exito(new ExtraccionEstructuradaDto(
            "Ficha de riesgos", new Dictionary<string, string?> { ["CIF"] = "B12345674", ["Fecha"] = "01/01/2026" }, 85, null)));
        var handler = new DetectarCamposPlantillaQueryHandler(
            new ExtractorAcroFormFalso([]), router, ContextoConConocimientoBasico(),
            Options.Create(new DeteccionPreviaDocumentoOptions { Activa = true }));

        var resultado = await handler.Handle(
            new DetectarCamposPlantillaQuery([1, 2, 3], FormatoOrigenPlantilla.PdfVisual), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().HaveCount(2);
        var cif = resultado.Valor.Single(c => c.EtiquetaVisible == "CIF");
        cif.FuenteDatoSugerida.Should().Be(FuenteDatoPlantilla.EmpresaCif);
        cif.Confianza.Should().Be(85); // min(100 exacta, 85 confianza general)
        cif.NombreCampoAcroForm.Should().BeNull();
        cif.Pagina.Should().Be(1);
    }

    [Fact]
    public async Task PdfVisual_propaga_el_fallo_del_router()
    {
        var router = new RouterFalso(Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear("Ia.Fallo", "proveedor caído")));
        var handler = new DetectarCamposPlantillaQueryHandler(
            new ExtractorAcroFormFalso([]), router, ContextoConConocimientoBasico(),
            Options.Create(new DeteccionPreviaDocumentoOptions { Activa = true }));

        var resultado = await handler.Handle(
            new DetectarCamposPlantillaQuery([1], FormatoOrigenPlantilla.PdfVisual), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Ia.Fallo");
    }

    [Fact]
    public async Task HtmlCss_no_esta_soportado_por_la_deteccion()
    {
        var handler = new DetectarCamposPlantillaQueryHandler(
            new ExtractorAcroFormFalso([]), new RouterQueLanzaSiSeInvoca(), ContextoConConocimientoBasico(),
            Options.Create(new DeteccionPreviaDocumentoOptions { Activa = false }));

        var resultado = await handler.Handle(
            new DetectarCamposPlantillaQuery([1], FormatoOrigenPlantilla.HtmlCss), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
    }

    private sealed class ExtractorAcroFormFalso(IReadOnlyList<CampoAcroFormDetectado> campos) : IExtractorCamposAcroFormService
    {
        public IReadOnlyList<CampoAcroFormDetectado> Extraer(byte[] pdfOriginal) => campos;
    }

    private sealed class RouterFalso(Result<ExtraccionEstructuradaDto> resultado) : IDocumentAIRouterService
    {
        public Task<Result<ExtraccionEstructuradaDto>> ProcesarAsync(
            byte[] contenido, string nombreArchivo, string tipoEsperado, Guid? documentoId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(resultado);
    }

    private sealed class RouterQueLanzaSiSeInvoca : IDocumentAIRouterService
    {
        public Task<Result<ExtraccionEstructuradaDto>> ProcesarAsync(
            byte[] contenido, string nombreArchivo, string tipoEsperado, Guid? documentoId = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("El path PdfConCampos no debería llamar nunca al router de IA.");
    }
}
