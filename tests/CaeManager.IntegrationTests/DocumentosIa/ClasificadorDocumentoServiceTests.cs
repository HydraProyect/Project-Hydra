using CaeManager.Application.Common;
using CaeManager.Infrastructure.DocumentosIa;
using CaeManager.Web.Reportes;
using FluentAssertions;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Xunit;

namespace CaeManager.IntegrationTests.DocumentosIa;

/// <summary>
/// PdfSharpClasificadorDocumentoService (Fase 1 de docs/ARQUITECTURA-IA-DOCUMENTAL.md)
/// se prueba contra PDFs reales generados con PdfSharp — una página con
/// texto dibujado de verdad (BT/Tj real) y una con solo un rectángulo
/// (sin ningún operador de texto), para no depender de heurísticas sobre
/// archivos de muestra externos. Reutiliza EmbeddedFontResolver de
/// CaeManager.Web (misma fuente DejaVu Sans embebida que usa
/// GeneradorPdfReporteDocumentos) para poder dibujar texto real en este
/// proceso de test, mismo motivo por el que otras pruebas de PDF de este
/// proyecto (ver DeteccionTrabajadoresServiceTests) evitaron antes dibujar
/// texto real sin registrar el resolver.
/// </summary>
public class ClasificadorDocumentoServiceTests
{
    static ClasificadorDocumentoServiceTests()
    {
        GlobalFontSettings.FontResolver ??= new EmbeddedFontResolver();
    }

    private readonly PdfSharpClasificadorDocumentoService _servicio = new();

    [Fact]
    public async Task Clasifica_como_digital_un_pdf_con_texto_real_en_todas_las_paginas()
    {
        var pdf = CrearPdf(true, true);

        var resultado = await _servicio.ClasificarAsync(pdf, "documento.pdf");

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Tipo.Should().Be(TipoContenidoDocumento.Digital);
        resultado.Valor.TotalPaginas.Should().Be(2);
        resultado.Valor.PaginasConTextoDigital.Should().OnlyContain(tieneTexto => tieneTexto);
    }

    [Fact]
    public async Task Clasifica_como_escaneado_un_pdf_sin_ningun_texto_embebido()
    {
        var pdf = CrearPdf(false, false);

        var resultado = await _servicio.ClasificarAsync(pdf, "documento.pdf");

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Tipo.Should().Be(TipoContenidoDocumento.Escaneado);
        resultado.Valor.PaginasConTextoDigital.Should().OnlyContain(tieneTexto => !tieneTexto);
    }

    [Fact]
    public async Task Clasifica_como_mixto_un_pdf_con_una_pagina_digital_y_otra_escaneada()
    {
        var pdf = CrearPdf(true, false);

        var resultado = await _servicio.ClasificarAsync(pdf, "documento.pdf");

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Tipo.Should().Be(TipoContenidoDocumento.Mixto);
        resultado.Valor.PaginasConTextoDigital.Should().Equal(true, false);
    }

    [Theory]
    [InlineData("foto.jpg")]
    [InlineData("foto.PNG")]
    [InlineData("foto.heic")]
    public async Task Clasifica_como_imagen_cualquier_archivo_que_no_sea_pdf(string nombreArchivo)
    {
        var resultado = await _servicio.ClasificarAsync([1, 2, 3], nombreArchivo);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Tipo.Should().Be(TipoContenidoDocumento.Imagen);
        resultado.Valor.TotalPaginas.Should().Be(1);
    }

    [Fact]
    public async Task Devuelve_fallo_ante_un_pdf_corrupto()
    {
        var resultado = await _servicio.ClasificarAsync([0x25, 0x50, 0x44, 0x46, 0x00, 0x00], "documento.pdf");

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ClasificacionDocumento.ArchivoInvalido");
    }

    private static byte[] CrearPdf(bool paginaConTexto, params bool[] paginasAdicionalesConTexto)
    {
        using var documento = new PdfDocument();

        void AgregarPagina(bool conTexto)
        {
            var pagina = documento.AddPage();
            using var graficos = XGraphics.FromPdfPage(pagina);
            if (conTexto)
            {
                var fuente = new XFont(EmbeddedFontResolver.NombreFuente, 12);
                graficos.DrawString("Documento de prueba con texto digital real.", fuente, XBrushes.Black, new XPoint(50, 50));
            }
            else
            {
                graficos.DrawRectangle(XBrushes.Gray, 10, 10, 100, 100);
            }
        }

        AgregarPagina(paginaConTexto);
        foreach (var conTexto in paginasAdicionalesConTexto)
            AgregarPagina(conTexto);

        using var salida = new MemoryStream();
        documento.Save(salida);
        return salida.ToArray();
    }
}
