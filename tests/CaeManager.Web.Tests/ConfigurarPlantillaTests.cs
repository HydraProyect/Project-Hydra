using CaeManager.Web.Features.Plantillas.Pages;
using FluentAssertions;
using PdfSharp.Pdf;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// ConfigurarPlantilla.ComprobarRecuentoDePaginas (REC-186) — el único de
/// los ocho sitios del inventario que vive en Web en vez de Infrastructure.
/// Pública solo para poder testearla directamente sin bUnit ni JS interop
/// (mismo patrón que <c>WebhookWhatsAppEndpoints.FirmaValida</c>, ver su
/// propio doc-comment). Mismo patrón "bombaIlegible" que REC-176
/// (ConversorArchivosPdfTests) para probar el MOMENTO del rechazo, no solo
/// que ocurra.
/// </summary>
public class ConfigurarPlantillaTests
{
    [Fact]
    public void Un_pdf_de_tamano_normal_no_se_rechaza()
    {
        using var documento = new PdfDocument();
        documento.AddPage();
        using var salida = new MemoryStream();
        documento.Save(salida);

        var comprobar = () => ConfigurarPlantilla.ComprobarRecuentoDePaginas(salida.ToArray());

        comprobar.Should().NotThrow();
    }

    [Fact]
    public void Un_pdf_que_declara_demasiadas_paginas_se_rechaza()
    {
        using var documento = new PdfDocument();
        for (var i = 0; i < 2001; i++)
            documento.AddPage();
        using var salida = new MemoryStream();
        documento.Save(salida);

        var comprobar = () => ConfigurarPlantilla.ComprobarRecuentoDePaginas(salida.ToArray());

        comprobar.Should().Throw<InvalidDataException>();
    }

    /// <summary>
    /// El PDF declara trailer→Root→Pages→Count en texto plano (999 999
    /// páginas), pero el resto del fichero es basura deliberada: no hay
    /// tabla xref válida. Medido en REC-176 contra PdfSharp 6.2.4:
    /// <c>PdfReader.Open</c> sobre estos bytes exactos lanza
    /// <c>PdfReaderException</c> en los cuatro <c>PdfDocumentOpenMode</c>
    /// públicos. Que <c>ComprobarRecuentoDePaginas</c> rechace con
    /// <see cref="InvalidDataException"/> SIN que <c>RasterizarPaginasAsync</c>
    /// llegue nunca a invocar PdfReader.Open es la prueba del momento — esta
    /// función ni siquiera importa PdfReader, así que si esto lanza,
    /// PdfSharp no ha llegado a intervenir.
    /// </summary>
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

        var comprobar = () => ConfigurarPlantilla.ComprobarRecuentoDePaginas(bombaIlegible);

        comprobar.Should().Throw<InvalidDataException>().WithMessage("*2000*");
    }
}
