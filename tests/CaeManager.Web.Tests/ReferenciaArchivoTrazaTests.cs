using CaeManager.Web.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El nombre de un archivo subido no es un dato técnico: en operativa real
/// llega con nombre, DNI y a veces la naturaleza médica del documento dentro.
/// Escribirlo en una traza lo duplica en Seq, en los ficheros de log y en sus
/// backups, con retenciones que no son la del documento.
/// </summary>
public class ReferenciaArchivoTrazaTests
{
    private const string NombreConDatosPersonales = "RECONOCIMIENTO MEDICO - JUAN PEREZ 12345678Z.pdf";

    [Theory]
    [InlineData("JUAN")]
    [InlineData("PEREZ")]
    [InlineData("12345678Z")]
    [InlineData("RECONOCIMIENTO")]
    [InlineData("MEDICO")]
    public void La_referencia_no_arrastra_ningun_dato_personal_del_nombre(string fragmento) =>
        ReferenciaArchivoTraza.De(NombreConDatosPersonales)
            .Should().NotContainEquivalentOf(fragmento);

    [Fact]
    public void Dos_lineas_del_mismo_archivo_comparten_referencia()
    {
        // Es la razón de ser del hash: poder seguir un archivo por una
        // incidencia entera sin haber escrito su nombre en ningún sitio.
        ReferenciaArchivoTraza.De(NombreConDatosPersonales)
            .Should().Be(ReferenciaArchivoTraza.De(NombreConDatosPersonales));
    }

    [Fact]
    public void Dos_archivos_distintos_no_comparten_referencia() =>
        ReferenciaArchivoTraza.De("uno.pdf")
            .Should().NotBe(ReferenciaArchivoTraza.De("dos.pdf"));

    [Fact]
    public void La_extension_se_conserva_porque_dice_que_ruta_de_conversion_fallo() =>
        ReferenciaArchivoTraza.De(NombreConDatosPersonales).Should().EndWith(".pdf");

    [Fact]
    public void La_extension_se_normaliza_para_no_partir_el_agrupado_en_seq() =>
        ReferenciaArchivoTraza.De("CONTRATO.DOCX").Should().EndWith(".docx");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Un_nombre_ausente_no_revienta_la_traza(string? nombreArchivo) =>
        ReferenciaArchivoTraza.De(nombreArchivo).Should().Be("archivo:sin-nombre");
}
