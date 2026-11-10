using System.Text;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Infrastructure.HerramientasPdf;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace CaeManager.Web.Tests.HerramientasPdf;

/// <summary>
/// Unir sobre PDF reales fabricados aquí, con datos inventados: PDFsharp inspecciona y une, y PdfPig lee el texto del
/// resultado para comprobar que las páginas llegan todas y en el orden pedido. Sin servicios externos.
/// </summary>
public sealed class UnionSobrePdfRealTests
{
    private readonly PdfSharpUnidorPdf _unidor = new(NullLogger<PdfSharpUnidorPdf>.Instance);

    /// <summary>Un PDF con una página por texto.</summary>
    private static byte[] Pdf(params string[] textos)
    {
        var constructor = new PdfDocumentBuilder();
        var fuente = constructor.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var texto in textos)
            constructor.AddPage(595, 842).AddText(texto, 14, new PdfPoint(40, 780), fuente);

        return constructor.Build();
    }

    private static string[] TextosDePaginas(byte[] pdf)
    {
        using var documento = PdfDocument.Open(pdf);
        return documento.GetPages().Select(p => string.Join(' ', p.GetWords().Select(w => w.Text))).ToArray();
    }

    [Fact]
    public void Une_en_el_orden_recibido_y_conserva_todas_las_paginas()
    {
        var resultado = _unidor.Unir([Pdf("Contrato A1", "Contrato A2"), Pdf("Seguro B1"), Pdf("Acta C1", "Acta C2", "Acta C3")]);

        resultado.EsExitoso.Should().BeTrue(resultado.EsExitoso ? "" : resultado.Error.Codigo);
        resultado.Valor.NumeroPaginas.Should().Be(6);
        TextosDePaginas(resultado.Valor.Contenido).Should().Equal(
            "Contrato A1", "Contrato A2", "Seguro B1", "Acta C1", "Acta C2", "Acta C3");
    }

    [Fact]
    public void Cambiar_el_orden_de_la_entrada_cambia_el_del_PDF_unido()
    {
        var a = Pdf("Primero");
        var b = Pdf("Segundo");

        TextosDePaginas(_unidor.Unir([a, b]).Valor.Contenido).Should().Equal("Primero", "Segundo");
        TextosDePaginas(_unidor.Unir([b, a]).Valor.Contenido).Should().Equal("Segundo", "Primero");
    }

    [Fact]
    public void Un_mismo_PDF_repetido_se_une_las_veces_que_aparece()
    {
        var a = Pdf("Anexo");

        TextosDePaginas(_unidor.Unir([a, Pdf("Cuerpo"), a]).Valor.Contenido).Should().Equal("Anexo", "Cuerpo", "Anexo");
    }

    [Fact]
    public void El_resultado_es_un_PDF_nuevo_que_se_vuelve_a_abrir_con_sus_paginas()
    {
        var unido = _unidor.Unir([Pdf("Uno"), Pdf("Dos")]).Valor.Contenido;

        _unidor.Inspeccionar(unido).Valor.Should().Be(new InfoPdfParaUnir(2, false));
    }

    [Fact]
    public void Inspeccionar_cuenta_paginas_y_marca_el_PDF_firmado_y_el_normal_no()
    {
        var normal = Pdf("Uno", "Dos", "Tres");
        var firmado = normal.Concat(Encoding.ASCII.GetBytes("\n%/ByteRange [0 1 2 3]\n")).ToArray();

        _unidor.Inspeccionar(normal).Valor.Should().Be(new InfoPdfParaUnir(3, false));
        _unidor.Inspeccionar(firmado).Valor.Should().Be(new InfoPdfParaUnir(3, true));
    }

    [Fact]
    public void Un_PDF_firmado_se_une_y_el_resultado_ya_no_lleva_la_firma()
    {
        var firmado = Pdf("Firmado").Concat(Encoding.ASCII.GetBytes("\n%/ByteRange [0 1 2 3]\n")).ToArray();

        var unido = _unidor.Unir([firmado, Pdf("Otro")]).Valor.Contenido;

        _unidor.Inspeccionar(unido).Valor.EstaFirmado.Should().BeFalse("el unido es un PDF nuevo y no conserva la firma");
    }

    [Fact]
    public void Un_PDF_con_contrasena_se_distingue_de_uno_roto_al_inspeccionar_y_al_unir()
    {
        var protegido = ConContrasena(Pdf("Secreto"));

        _unidor.Inspeccionar(protegido).Error.Codigo.Should().Be(ErroresUnirPdf.ConContrasena);
        _unidor.Unir([Pdf("Uno"), protegido]).Error.Codigo.Should().Be(ErroresUnirPdf.ConContrasena);
        _unidor.Inspeccionar(Encoding.ASCII.GetBytes("esto no es un PDF")).Error.Codigo.Should().Be(ErroresUnirPdf.Ilegible);
        _unidor.Inspeccionar([]).Error.Codigo.Should().Be(ErroresUnirPdf.Ilegible);
    }

    [Fact]
    public void Si_las_paginas_del_unido_pasan_del_maximo_no_se_une()
    {
        var trescientas = Pdf(Enumerable.Range(1, 300).Select(i => $"Pagina {i}").ToArray());

        var resultado = _unidor.Unir([trescientas, trescientas]);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.DemasiadasPaginas);
    }

    [Fact]
    public void Quinientas_paginas_justas_si_se_unen()
    {
        var doscientas = Pdf(Enumerable.Range(1, 200).Select(i => $"Pagina {i}").ToArray());
        var trescientas = Pdf(Enumerable.Range(1, 300).Select(i => $"Hoja {i}").ToArray());

        var resultado = _unidor.Unir([doscientas, trescientas]);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.NumeroPaginas.Should().Be(LimitesUnionPdf.MaximoPaginasTotales);
    }

    [Fact]
    public void Un_PDF_de_mas_del_tope_total_de_bytes_no_se_abre()
    {
        var pasado = new byte[LimitesUnionPdf.TamanoMaximoTotalBytes + 1];
        Pdf("Uno").CopyTo(pasado, 0);

        _unidor.Inspeccionar(pasado).Error.Codigo.Should().Be(ErroresUnirPdf.DemasiadoGrande);
    }

    private static byte[] ConContrasena(byte[] pdf)
    {
        using var entrada = new MemoryStream(pdf);
        using var documento = PdfReader.Open(entrada, PdfDocumentOpenMode.Modify);
        documento.SecuritySettings.UserPassword = "usuario";
        documento.SecuritySettings.OwnerPassword = "propietario";
        using var salida = new MemoryStream();
        documento.Save(salida, closeStream: false);
        return salida.ToArray();
    }
}
