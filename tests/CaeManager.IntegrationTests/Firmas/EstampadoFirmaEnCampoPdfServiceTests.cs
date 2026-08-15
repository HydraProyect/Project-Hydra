using CaeManager.Infrastructure.Firmas;
using CaeManager.Web.Reportes;
using FluentAssertions;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace CaeManager.IntegrationTests.Firmas;

public class EstampadoFirmaEnCampoPdfServiceTests
{
    // 1x1 PNG válido mínimo (píxel rojo) — no depende de SkiaSharp (PR-6),
    // solo hace falta que XImage.FromStream pueda leerlo.
    private static readonly byte[] TrazoPngDePrueba = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    static EstampadoFirmaEnCampoPdfServiceTests()
    {
        GlobalFontSettings.FontResolver ??= new EmbeddedFontResolver();
    }

    private static byte[] CrearPdfDeUnaPagina()
    {
        using var documento = new PdfDocument();
        var pagina = documento.AddPage();
        using (var graficos = XGraphics.FromPdfPage(pagina))
            graficos.DrawString("Documento original", new XFont(EmbeddedFontResolver.NombreFuente, 12), XBrushes.Black, new XPoint(20, 20));
        using var salida = new MemoryStream();
        documento.Save(salida);
        return salida.ToArray();
    }

    private static int ContarPaginas(byte[] pdf)
    {
        using var flujo = new MemoryStream(pdf);
        using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Import);
        return documento.PageCount;
    }

    [Fact]
    public void Estampar_añade_una_pagina_nueva_al_final()
    {
        var servicio = new EstampadoFirmaEnCampoPdfService();
        var original = CrearPdfDeUnaPagina();

        var resultado = servicio.Estampar(original, TrazoPngDePrueba, null, "Juan Pérez", "GestorCae", DateTime.UtcNow, "Madrid");

        ContarPaginas(resultado).Should().Be(ContarPaginas(original) + 1);
    }

    [Fact]
    public void Estampar_produce_un_contenido_distinto_del_original()
    {
        var servicio = new EstampadoFirmaEnCampoPdfService();
        var original = CrearPdfDeUnaPagina();

        var resultado = servicio.Estampar(original, TrazoPngDePrueba, null, "Juan Pérez", "GestorCae", DateTime.UtcNow, null);

        resultado.Should().NotBeEquivalentTo(original);
    }

    [Fact]
    public void Estampar_sin_ubicacion_no_falla()
    {
        var servicio = new EstampadoFirmaEnCampoPdfService();
        var original = CrearPdfDeUnaPagina();

        var accion = () => servicio.Estampar(original, TrazoPngDePrueba, null, "Juan Pérez", "GestorCae", DateTime.UtcNow, null);

        accion.Should().NotThrow();
    }

    [Fact]
    public void Estampar_con_sello_dibuja_ambas_imagenes_sin_fallar()
    {
        var servicio = new EstampadoFirmaEnCampoPdfService();
        var original = CrearPdfDeUnaPagina();

        var accion = () => servicio.Estampar(original, TrazoPngDePrueba, TrazoPngDePrueba, "Juan Pérez", "GestorCae", DateTime.UtcNow, null);

        accion.Should().NotThrow();
    }
}
