using CaeManager.Web.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-16 de docs/business/MATURITY_REVIEW.md: la extensión del nombre de
/// archivo es trivial de falsificar (renombrar cualquier cosa a ".pdf") —
/// estos casos cubren que la firma de bytes real manda, no el nombre.
/// </summary>
public class ValidadorFirmaArchivoTests
{
    private static readonly byte[] BytesPdfValidos = "%PDF-1.7\nresto del archivo"u8.ToArray();
    private static readonly byte[] BytesJpegValidos = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
    private static readonly byte[] BytesPngValidos = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];
    private static readonly byte[] BytesZipValidos = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00];
    private static readonly byte[] BytesTextoPlano = "esto no es un archivo binario reconocido"u8.ToArray();

    [Theory]
    [InlineData("informe.pdf")]
    [InlineData("INFORME.PDF")]
    public void Un_pdf_con_firma_correcta_es_valido(string nombreArchivo) =>
        ValidadorFirmaArchivo.TieneFirmaValida(BytesPdfValidos, nombreArchivo).Should().BeTrue();

    [Theory]
    [InlineData("foto.jpg")]
    [InlineData("foto.jpeg")]
    public void Una_imagen_jpeg_con_firma_correcta_es_valida(string nombreArchivo) =>
        ValidadorFirmaArchivo.TieneFirmaValida(BytesJpegValidos, nombreArchivo).Should().BeTrue();

    [Fact]
    public void Una_imagen_png_con_firma_correcta_es_valida() =>
        ValidadorFirmaArchivo.TieneFirmaValida(BytesPngValidos, "captura.png").Should().BeTrue();

    [Fact]
    public void Un_word_con_firma_zip_correcta_es_valido() =>
        ValidadorFirmaArchivo.TieneFirmaValida(BytesZipValidos, "contrato.docx").Should().BeTrue();

    [Fact]
    public void Un_ejecutable_renombrado_a_pdf_no_pasa_la_firma()
    {
        byte[] cabeceraExe = [0x4D, 0x5A, 0x90, 0x00]; // "MZ" — cabecera PE de Windows
        ValidadorFirmaArchivo.TieneFirmaValida(cabeceraExe, "factura.pdf").Should().BeFalse();
    }

    [Fact]
    public void Un_docx_renombrado_como_jpg_no_pasa_la_firma() =>
        ValidadorFirmaArchivo.TieneFirmaValida(BytesZipValidos, "foto.jpg").Should().BeFalse();

    [Fact]
    public void Una_imagen_renombrada_como_docx_no_pasa_la_firma() =>
        ValidadorFirmaArchivo.TieneFirmaValida(BytesJpegValidos, "contrato.docx").Should().BeFalse();

    [Fact]
    public void Texto_plano_con_extension_pdf_no_pasa_la_firma() =>
        ValidadorFirmaArchivo.TieneFirmaValida(BytesTextoPlano, "documento.pdf").Should().BeFalse();

    [Fact]
    public void Un_archivo_vacio_no_pasa_ninguna_firma() =>
        ValidadorFirmaArchivo.TieneFirmaValida([], "documento.pdf").Should().BeFalse();

    [Fact]
    public void Un_archivo_mas_corto_que_la_firma_esperada_no_revienta_y_no_es_valido() =>
        ValidadorFirmaArchivo.TieneFirmaValida([0xFF, 0xD8], "foto.jpg").Should().BeFalse();

    [Fact]
    public void Una_extension_no_reconocida_no_es_valida() =>
        ValidadorFirmaArchivo.TieneFirmaValida(BytesPdfValidos, "documento.txt").Should().BeFalse();
}
