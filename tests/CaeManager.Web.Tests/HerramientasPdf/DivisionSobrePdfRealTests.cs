using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Application.Trabajadores.Extraccion;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.DocumentosIa;
using CaeManager.Infrastructure.HerramientasPdf;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace CaeManager.Web.Tests.HerramientasPdf;

/// <summary>
/// Lectura, detección y extracción sobre PDF reales fabricados aquí, con datos inventados: PDFsharp abre y reparte, y
/// PdfPig lee el texto. Sin servicios externos.
/// </summary>
public sealed class DivisionSobrePdfRealTests
{
    private static readonly Guid EmpresaId = Guid.NewGuid();
    private static readonly Guid HaciendaId = Guid.NewGuid();
    private static readonly Guid FormacionId = Guid.NewGuid();
    private static readonly Guid AnaId = Guid.NewGuid();

    private static readonly CatalogoDivisionPdf Catalogo = new(
        new EmpresaDivisionPdf(EmpresaId, "Montajes Norte S.L.", "B12345678"), [],
        [new TrabajadorDivisionPdf(AnaId, "Ana", "Perez Gil", null, EmpresaId)], [],
        [
            new TipoDocumentoDivisionPdf(HaciendaId, "Certificado de Hacienda", AmbitoAplicacion.Empresa, false, null, []),
            new TipoDocumentoDivisionPdf(FormacionId, "Formacion PRL", AmbitoAplicacion.Trabajador, false, null, []),
        ]);

    private readonly PdfSharpDivisorPdf _divisor = new(NullLogger<PdfSharpDivisorPdf>.Instance);
    private readonly PdfPigExtractorPalabrasPdf _extractor = new(NullLogger<PdfPigExtractorPalabrasPdf>.Instance);

    /// <summary>Un PDF con una página por entrada; una entrada vacía es una página sin texto.</summary>
    private static byte[] Pdf(params string[][] paginas)
    {
        var constructor = new PdfDocumentBuilder();
        var fuente = constructor.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var lineas in paginas)
        {
            var pagina = constructor.AddPage(595, 842);
            for (var i = 0; i < lineas.Length; i++)
                pagina.AddText(lineas[i], i == 0 ? 18 : 10, new PdfPoint(40, 800 - (i * 24)), fuente);
        }

        return constructor.Build();
    }

    private static readonly string[] Hacienda = ["Certificado de Hacienda", "Titular Montajes Norte", "CIF B-12345678"];
    private static readonly string[] Formacion = ["Formacion PRL", "Alumna Ana Perez Gil", "Curso de 60 horas"];

    private IReadOnlyList<TramoDetectado> Detectar(byte[] pdf)
    {
        using var abierto = _divisor.Abrir(pdf).Valor;
        var palabras = _extractor.ExtraerPalabras(pdf, LimitesDivisionPdf.TamanoMaximoOriginalBytes);
        palabras.EsExitoso.Should().BeTrue(palabras.Error.Codigo);

        return DetectorTramosPdf.Detectar(palabras.Valor, abierto.NumeroPaginas, Catalogo);
    }

    [Fact]
    public void Un_PDF_digital_con_varios_documentos_se_parte_con_tipo_titular_y_motivo()
    {
        var tramos = Detectar(Pdf(Hacienda, ["Anexo sin cabecera", "mas texto de relleno", "Pagina 2 de 2"], Formacion));

        tramos.Should().HaveCount(2);
        tramos[0].Should().BeEquivalentTo(new TramoDetectado(
            1, 2, HaciendaId, "Certificado de Hacienda", ClaseTitular.EmpresaGestionada, EmpresaId, "Montajes Norte S.L.",
            MotivoDeteccion.CifEmpresaGestionada, 1));
        tramos[1].Should().BeEquivalentTo(new TramoDetectado(
            3, 3, FormacionId, "Formacion PRL", ClaseTitular.Trabajador, AnaId, "Ana Perez Gil", MotivoDeteccion.Nombre, 3));
    }

    [Fact]
    public void Un_PDF_sin_texto_queda_entero_sin_identificar()
    {
        var tramos = Detectar(Pdf([], [], []));

        tramos.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new TramoDetectado(1, 3, null, null, null, null, null, MotivoDeteccion.SinTexto, null));
    }

    [Fact]
    public void Un_PDF_mezclado_separa_lo_digital_de_lo_escaneado()
    {
        var tramos = Detectar(Pdf(Hacienda, [], [], Formacion));

        tramos.Select(t => (t.PaginaInicial, t.PaginaFinal, t.Motivo)).Should().Equal(
            (1, 1, MotivoDeteccion.CifEmpresaGestionada),
            (2, 3, MotivoDeteccion.SinTexto),
            (4, 4, MotivoDeteccion.Nombre));
    }

    [Fact]
    public void Extraer_un_rango_conserva_el_numero_de_paginas_y_su_contenido()
    {
        var pdf = Pdf(Hacienda, ["Segunda pagina del paquete"], ["Tercera pagina del paquete"], Formacion);
        using var abierto = _divisor.Abrir(pdf).Valor;

        var tramo = abierto.ExtraerPaginas(2, 3);

        tramo.EsExitoso.Should().BeTrue();
        var paginas = _extractor.ExtraerPalabras(tramo.Valor).Valor;
        paginas.Should().HaveCount(2);
        Texto(paginas[0]).Should().Be("Segunda pagina del paquete");
        Texto(paginas[1]).Should().Be("Tercera pagina del paquete");
    }

    private static string Texto(PaginaPdf pagina) =>
        string.Join(' ', pagina.Palabras.Select(p => p.Texto.Trim()).Where(t => t.Length > 0));

    [Fact]
    public void Del_mismo_PDF_abierto_salen_varios_tramos_y_todo_el_rango_es_valido()
    {
        var pdf = Pdf(Hacienda, Formacion, Hacienda);
        using var abierto = _divisor.Abrir(pdf).Valor;

        Paginas(abierto.ExtraerPaginas(1, 1).Valor).Should().Be(1);
        Paginas(abierto.ExtraerPaginas(2, 3).Valor).Should().Be(2);
        Paginas(abierto.ExtraerPaginas(1, 3).Valor).Should().Be(3);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 4)]
    public void Un_rango_fuera_del_PDF_falla(int inicial, int final)
    {
        using var abierto = _divisor.Abrir(Pdf(Hacienda, Formacion, Hacienda)).Valor;

        abierto.ExtraerPaginas(inicial, final).Error.Codigo.Should().Be(ErroresDivisorPdf.RangoInvalido);
    }

    [Fact]
    public void Un_PDF_firmado_se_marca_para_avisar_y_uno_normal_no()
    {
        var normal = Pdf(Hacienda);
        var firmado = normal.Concat(Encoding.ASCII.GetBytes("\n%/ByteRange [0 1 2 3]\n")).ToArray();

        using (var abierto = _divisor.Abrir(normal).Valor)
            abierto.EstaFirmado.Should().BeFalse();

        using (var abierto = _divisor.Abrir(firmado).Valor)
            abierto.EstaFirmado.Should().BeTrue();
    }

    [Fact]
    public void Un_PDF_con_contrasena_se_distingue_de_uno_roto()
    {
        var protegido = ConContrasena(Pdf(Hacienda));

        _divisor.Abrir(protegido).Error.Codigo.Should().Be(ErroresDivisorPdf.ConContrasena);
        _divisor.Abrir(Encoding.ASCII.GetBytes("esto no es un PDF")).Error.Codigo.Should().Be(ErroresDivisorPdf.Ilegible);
        _divisor.Abrir([]).Error.Codigo.Should().Be(ErroresDivisorPdf.Ilegible);
    }

    [Fact]
    public void Acepta_originales_de_hasta_50_MB_y_rechaza_los_mayores()
    {
        var justo = Rellenar(Pdf(Hacienda), LimitesDivisionPdf.TamanoMaximoOriginalBytes);

        using (var abierto = _divisor.Abrir(justo).Valor)
            abierto.NumeroPaginas.Should().Be(1);

        _extractor.ExtraerPalabras(justo).Error.Codigo.Should().Be("ExtractorPalabrasPdf.ArchivoDemasiadoGrande",
            "el tope de 10 MB de los demás llamadores no cambia");
        _extractor.ExtraerPalabras(justo, LimitesDivisionPdf.TamanoMaximoOriginalBytes).EsExitoso.Should().BeTrue();

        var pasado = Rellenar(Pdf(Hacienda), LimitesDivisionPdf.TamanoMaximoOriginalBytes + 1);
        _divisor.Abrir(pasado).Error.Codigo.Should().Be(ErroresDivisorPdf.DemasiadoGrande);
        _extractor.ExtraerPalabras(pasado, LimitesDivisionPdf.TamanoMaximoOriginalBytes).EsFallido.Should().BeTrue();
    }

    /// <summary>Rellena con un comentario detrás de %%EOF: mismo PDF válido, con el tamaño exacto pedido.</summary>
    private static byte[] Rellenar(byte[] pdf, long tamano)
    {
        var relleno = new byte[tamano];
        pdf.CopyTo(relleno, 0);
        Array.Fill(relleno, (byte)' ', pdf.Length, relleno.Length - pdf.Length);
        return relleno;
    }

    private static int Paginas(byte[] pdf)
    {
        using var flujo = new MemoryStream(pdf);
        using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Import);
        return documento.PageCount;
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
