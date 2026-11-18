using System.Text;
using CaeManager.Application.Common.Archivos;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El PDF de varias páginas de la siembra de Comunicaciones (<see cref="ComunicacionesDemoPdf"/>) es un PDF de verdad: lo abren dos
/// lectores independientes con una página por texto (así se puede dividir en tramos) y pasa la comprobación de firma que la
/// aplicación aplica a todo archivo.
/// </summary>
public class ComunicacionesDemoPdfTests
{
    private static readonly string[] Paginas = ["Página uno · demo", "Página dos · demo", "Página tres · demo", "Página cuatro · demo"];

    [Fact]
    public void Tiene_una_pagina_por_texto_y_cada_una_dice_su_texto()
    {
        var bytes = ComunicacionesDemoPdf.Generar(Paginas);

        using var pdfPig = PdfDocument.Open(bytes);
        pdfPig.NumberOfPages.Should().Be(Paginas.Length);
        for (var i = 0; i < Paginas.Length; i++)
            pdfPig.GetPage(i + 1).Text.Should().Be(Paginas[i], "MEDIDO: lo que lee un lector de PDF en cada página es su texto");

        using var flujo = new MemoryStream(bytes);
        using var pdfSharp = PdfReader.Open(flujo, PdfDocumentOpenMode.Import);
        pdfSharp.PageCount.Should().Be(Paginas.Length);

        ValidadorFirmaArchivo.TieneFormatoDeDocumento(bytes, "documento.pdf").Should().BeTrue("misma comprobación que la subida real");
    }

    [Fact]
    public void La_tabla_xref_apunta_a_cada_objeto_y_startxref_a_la_tabla()
    {
        var bytes = ComunicacionesDemoPdf.Generar(Paginas);
        var pdf = Encoding.Latin1.GetString(bytes);
        var objetos = 3 + 2 * Paginas.Length;

        var inicioTabla = int.Parse(pdf[(pdf.LastIndexOf("startxref\n", StringComparison.Ordinal) + "startxref\n".Length)..].Split('\n')[0]);
        pdf[inicioTabla..].Should().StartWith($"xref\n0 {objetos + 1}\n");

        var entradas = pdf[inicioTabla..].Split('\n').Skip(3).Take(objetos).ToList();
        entradas.Should().HaveCount(objetos);
        for (var i = 0; i < entradas.Count; i++)
        {
            var desplazamiento = int.Parse(entradas[i][..10]);
            pdf[desplazamiento..].Should().StartWith($"{i + 1} 0 obj\n");
        }
    }

    [Fact]
    public void Mismos_textos_mismos_bytes_y_el_texto_se_escapa()
    {
        ComunicacionesDemoPdf.Generar(Paginas).Should().Equal(ComunicacionesDemoPdf.Generar(Paginas));

        var escapado = Encoding.Latin1.GetString(ComunicacionesDemoPdf.Generar([@"a (b) \c"]));
        escapado.Should().Contain(@"(a \(b\) \\c) Tj");
    }

    [Fact]
    public void Sin_paginas_o_con_una_vacia_no_genera_un_PDF()
    {
        var sinPaginas = () => ComunicacionesDemoPdf.Generar([]);
        sinPaginas.Should().Throw<ArgumentException>();

        var conVacia = () => ComunicacionesDemoPdf.Generar(["uno", " "]);
        conVacia.Should().Throw<ArgumentException>();
    }
}
