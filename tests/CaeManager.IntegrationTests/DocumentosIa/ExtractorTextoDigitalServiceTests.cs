using CaeManager.Infrastructure.DocumentosIa;
using CaeManager.Web.Reportes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Xunit;

namespace CaeManager.IntegrationTests.DocumentosIa;

/// <summary>
/// PdfSharpExtractorTextoDigitalService se prueba contra PDFs reales con
/// texto dibujado de verdad, mismo criterio y fuente embebida que
/// ClasificadorDocumentoServiceTests.
/// </summary>
public class ExtractorTextoDigitalServiceTests
{
    static ExtractorTextoDigitalServiceTests()
    {
        GlobalFontSettings.FontResolver ??= new EmbeddedFontResolver();
    }

    private readonly PdfSharpExtractorTextoDigitalService _servicio =
        new(NullLogger<PdfSharpExtractorTextoDigitalService>.Instance);

    [Fact]
    public void Extrae_el_texto_real_de_un_pdf_digital()
    {
        var pdf = CrearPdfConTexto("Reconocimiento medico apto sin restricciones");

        var resultado = _servicio.ExtraerTextoPorPagina(pdf);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().ContainSingle().Which.Should().Contain("Reconocimiento medico apto sin restricciones");
    }

    [Fact]
    public void Extrae_texto_de_varias_paginas_en_orden()
    {
        var pdf = CrearPdfConVariasPaginas("Primera pagina de texto", "Segunda pagina de texto");

        var resultado = _servicio.ExtraerTextoPorPagina(pdf);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().HaveCount(2);
        resultado.Valor[0].Should().Contain("Primera pagina de texto");
        resultado.Valor[1].Should().Contain("Segunda pagina de texto");
    }

    [Fact]
    public void Devuelve_texto_vacio_para_una_pagina_sin_ningun_texto_embebido()
    {
        using var documento = new PdfDocument();
        var pagina = documento.AddPage();
        using (var graficos = XGraphics.FromPdfPage(pagina))
            graficos.DrawRectangle(XBrushes.Gray, 10, 10, 100, 100);
        using var salida = new MemoryStream();
        documento.Save(salida);

        var resultado = _servicio.ExtraerTextoPorPagina(salida.ToArray());

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().ContainSingle().Which.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Devuelve_fallo_ante_un_pdf_corrupto()
    {
        var resultado = _servicio.ExtraerTextoPorPagina([0x25, 0x50, 0x44, 0x46, 0x00, 0x00]);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ExtractorTextoDigital.ArchivoInvalido");
    }

    // ── REC-186: mismo patrón que ClasificadorDocumentoServiceTests — el
    // código "DemasiadasPaginas" (no "ArchivoInvalido") es la prueba de que
    // el rechazo ocurre ANTES de PdfReader.Open, no como efecto colateral
    // de un PdfReaderException capturado. ──────────────────────────────────

    [Fact]
    public void Rechaza_antes_de_abrir_un_pdf_que_declara_demasiadas_paginas()
    {
        var bombaIlegible = System.Text.Encoding.Latin1.GetBytes(
            "%PDF-1.4\n" +
            "1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n" +
            "2 0 obj\n<</Type/Pages/Count 999999/Kids[]>>\nendobj\n" +
            "AQUI NO HAY TABLA XREF VALIDA, SOLO BASURA DELIBERADA\n" +
            "trailer\n<</Root 1 0 R/Size 3>>\n" +
            "startxref\n0\n%%EOF");

        var resultado = _servicio.ExtraerTextoPorPagina(bombaIlegible);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ExtractorTextoDigital.DemasiadasPaginas");
    }

    private static byte[] CrearPdfConTexto(string texto)
    {
        using var documento = new PdfDocument();
        var pagina = documento.AddPage();
        using var graficos = XGraphics.FromPdfPage(pagina);
        var fuente = new XFont(EmbeddedFontResolver.NombreFuente, 12);
        graficos.DrawString(texto, fuente, XBrushes.Black, new XPoint(50, 50));
        using var salida = new MemoryStream();
        documento.Save(salida);
        return salida.ToArray();
    }

    private static byte[] CrearPdfConVariasPaginas(params string[] textosPorPagina)
    {
        using var documento = new PdfDocument();
        foreach (var texto in textosPorPagina)
        {
            var pagina = documento.AddPage();
            using var graficos = XGraphics.FromPdfPage(pagina);
            var fuente = new XFont(EmbeddedFontResolver.NombreFuente, 12);
            graficos.DrawString(texto, fuente, XBrushes.Black, new XPoint(50, 50));
        }
        using var salida = new MemoryStream();
        documento.Save(salida);
        return salida.ToArray();
    }
}
