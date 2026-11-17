using System.Text;
using CaeManager.Application.Common.Archivos;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El PDF de prueba de la siembra Inbound (<see cref="InboundDemoPdf"/>) es un PDF de verdad: lo abren dos lectores
/// independientes, su única página dice la constante y pasa la comprobación de firma que la aplicación aplica a todo archivo
/// que entra como Documento. Sin esto, un PDF mal formado llegaría al Gestor CAE como «archivo no válido».
/// </summary>
public class InboundDemoPdfTests
{
    [Theory]
    [InlineData(false, "Documento de prueba · demo")]
    [InlineData(true, "Apto · documento de prueba")]
    public void Es_un_PDF_de_una_pagina_con_solo_su_texto(bool soloConclusionAptitud, string textoEsperado)
    {
        var texto = InboundDemoPdf.TextoDe(soloConclusionAptitud);
        texto.Should().Be(textoEsperado);

        var bytes = InboundDemoPdf.Generar(texto);

        using var pdfPig = PdfDocument.Open(bytes);
        pdfPig.NumberOfPages.Should().Be(1);
        pdfPig.GetPage(1).Text.Should().Be(textoEsperado, "MEDIDO: lo que lee un lector de PDF es la constante, nada más");

        using var flujo = new MemoryStream(bytes);
        using var pdfSharp = PdfReader.Open(flujo, PdfDocumentOpenMode.Import);
        pdfSharp.PageCount.Should().Be(1);

        ValidadorFirmaArchivo.TieneFormatoDeDocumento(bytes, "documento.pdf").Should().BeTrue("misma comprobación que la subida real");
    }

    [Fact]
    public void La_tabla_xref_apunta_a_cada_objeto_y_startxref_a_la_tabla()
    {
        var bytes = InboundDemoPdf.Generar(InboundDemoPdf.TextoGeneral);
        var pdf = Encoding.Latin1.GetString(bytes);

        var inicioTabla = int.Parse(pdf[(pdf.LastIndexOf("startxref\n", StringComparison.Ordinal) + "startxref\n".Length)..].Split('\n')[0]);
        pdf[inicioTabla..].Should().StartWith("xref\n0 6\n");

        var entradas = pdf[inicioTabla..].Split('\n').Skip(3).Take(5).ToList();
        entradas.Should().HaveCount(5);
        for (var i = 0; i < entradas.Count; i++)
        {
            entradas[i].Should().HaveLength(19, "diez dígitos, generación y n, más el espacio previo al salto de línea");
            var desplazamiento = int.Parse(entradas[i][..10]);
            pdf[desplazamiento..].Should().StartWith($"{i + 1} 0 obj\n");
        }
    }

    [Fact]
    public void Mismo_texto_mismos_bytes_y_el_texto_se_escapa()
    {
        InboundDemoPdf.Generar(InboundDemoPdf.TextoGeneral).Should().Equal(InboundDemoPdf.Generar(InboundDemoPdf.TextoGeneral));

        var escapado = Encoding.Latin1.GetString(InboundDemoPdf.Generar(@"a (b) \c"));
        escapado.Should().Contain(@"(a \(b\) \\c) Tj");
    }

    [Fact]
    public void Un_texto_vacio_no_genera_un_PDF()
    {
        var generar = () => InboundDemoPdf.Generar(" ");
        generar.Should().Throw<ArgumentException>();
    }
}
