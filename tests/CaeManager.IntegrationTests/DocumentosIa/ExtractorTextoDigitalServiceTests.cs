using CaeManager.Infrastructure.DocumentosIa;
using CaeManager.Web.Reportes;
using FluentAssertions;
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

    private readonly PdfSharpExtractorTextoDigitalService _servicio = new();

    [Fact]
    public void Extrae_el_texto_real_de_un_pdf_digital()
    {
        var pdf = CrearPdfConTexto("Reconocimiento medico apto sin restricciones");

        var resultado = _servicio.ExtraerTexto(pdf);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Contain("Reconocimiento medico apto sin restricciones");
    }

    [Fact]
    public void Extrae_texto_de_varias_paginas_en_orden()
    {
        var pdf = CrearPdfConVariasPaginas("Primera pagina de texto", "Segunda pagina de texto");

        var resultado = _servicio.ExtraerTexto(pdf);

        resultado.EsExitoso.Should().BeTrue();
        var indicePrimera = resultado.Valor.IndexOf("Primera pagina de texto", StringComparison.Ordinal);
        var indiceSegunda = resultado.Valor.IndexOf("Segunda pagina de texto", StringComparison.Ordinal);
        indicePrimera.Should().BeGreaterThanOrEqualTo(0);
        indiceSegunda.Should().BeGreaterThan(indicePrimera);
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

        var resultado = _servicio.ExtraerTexto(salida.ToArray());

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Devuelve_fallo_ante_un_pdf_corrupto()
    {
        var resultado = _servicio.ExtraerTexto([0x25, 0x50, 0x44, 0x46, 0x00, 0x00]);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ExtractorTextoDigital.ArchivoInvalido");
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
