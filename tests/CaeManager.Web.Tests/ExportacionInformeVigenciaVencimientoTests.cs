using System.Text;
using CaeManager.Application.Reportes.Queries;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Reportes;
using ClosedXML.Excel;
using FluentAssertions;
using PdfSharp.Fonts;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lee el CONTENIDO de un PDF o un Excel de «Informe de vigencia documental» ya generado (no solo que no falle):
/// el PDF dibuja cada celda con un <c>Tj</c> en una fuente TrueType WinAnsi, y el Excel se abre con ClosedXML.
/// </summary>
public static class LectorInformeVigenciaExportado
{
    private static readonly object CandadoFuente = new();

    /// <summary>
    /// Genera el PDF de vigencia de <paramref name="informe"/> y devuelve las filas de su tabla (Estado, Trabajador,
    /// Empresa, Tipo de documento, Vencimiento); admite una sola página. Registra la fuente embebida antes de generar,
    /// como hace Program.cs.
    /// </summary>
    public static IReadOnlyList<string[]> FilasDelPdf(InformeVigenciaDto informe)
    {
        // Global y no atómico: dos clases de test que generan PDF en paralelo no pueden asignarlo a la vez.
        lock (CandadoFuente) GlobalFontSettings.FontResolver ??= new EmbeddedFontResolver();
        var pdf = ConstructorInformeArchivos.PdfVigencia(informe, incluirVigentes: true);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var winAnsi = Encoding.GetEncoding(1252);

        using var flujo = new MemoryStream(pdf);
        using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Import);
        documento.PageCount.Should().Be(1, "este lector solo cuenta con la cabecera de la primera página");

        var textos = ContentReader.ReadContent(documento.Pages[0]).OfType<COperator>()
            .Where(o => o.OpCode.Name == "Tj")
            .Select(o => (CString)o.Operands[0])
            .Select(c => winAnsi.GetString(c.Value.Select(ch => (byte)ch).ToArray()))
            .ToList();

        const int columnas = 5;
        textos.Skip(2).Take(columnas).Should().Equal(["Estado", "Trabajador", "Empresa", "Tipo de documento", "Vencimiento"],
            "tras el título y el subtítulo va la cabecera de la tabla");
        return textos.Skip(2 + columnas).Chunk(columnas).ToList();
    }

    /// <summary>La hoja «Informe» del Excel de vigencia.</summary>
    public static IXLWorksheet HojaDelExcel(byte[] excel)
    {
        var libro = new XLWorkbook(new MemoryStream(excel));
        return libro.Worksheet("Informe");
    }
}

/// <summary>
/// La columna «Vencimiento» de las exportaciones del informe de vigencia dice lo mismo que la hoja de la vista previa
/// (decisión del propietario, 2026-10-03: «Sin confirmar» comunica un estado conocido; «—» lo oculta): la fecha, o, sin
/// fecha, <c>EstadoDocumentoUi.TextoSinFechaDeVencimiento</c>. En Excel, texto en la celda solo en «Sin confirmar» y
/// «Sin caducidad»; en cualquier otro estado sin fecha, la celda vacía como siempre.
/// </summary>
public class ExportacionInformeVigenciaVencimientoTests
{
    private static FilaReporteDocumentoDto Fila(string trabajador, DateOnly? vence, EstadoDocumento estado) =>
        new(Guid.NewGuid(), trabajador, "Instalaciones Vega S.L.", "Reconocimiento médico", vence, estado);

    private static InformeVigenciaDto Informe() => new("Todo el tenant", [
        Fila("Ana Con Fecha", new DateOnly(2027, 3, 1), EstadoDocumento.Vigente),
        Fila("Bea Sin Confirmar", null, EstadoDocumento.SinConfirmar),
        Fila("Carlos Sin Caducidad", null, EstadoDocumento.SinCaducidad),
        Fila("Dora Falta", null, EstadoDocumento.Faltante)
    ]);

    [Fact]
    public void El_PDF_rotula_Sin_confirmar_y_Sin_caducidad_en_Vencimiento_y_deja_la_raya_en_el_resto()
    {
        var filas = LectorInformeVigenciaExportado.FilasDelPdf(Informe());

        filas.Select(f => (f[1], f[4])).Should().Equal(
            ("Ana Con Fecha", "01/03/2027"),
            ("Bea Sin Confirmar", "Sin confirmar"),
            ("Carlos Sin Caducidad", "Sin caducidad"),
            ("Dora Falta", "—"));
        filas.Select(f => f[0]).Should().Equal("Vigente", "Sin confirmar", "Sin caducidad", "Pendiente");
    }

    [Fact]
    public void El_Excel_pone_fecha_real_o_el_rotulo_solo_en_Sin_confirmar_y_Sin_caducidad_y_deja_vacio_el_resto()
    {
        var hoja = LectorInformeVigenciaExportado.HojaDelExcel(ConstructorInformeArchivos.ExcelVigencia(Informe()));

        hoja.Cell(2, 2).GetString().Should().Be("Ana Con Fecha");
        hoja.Cell(2, 5).DataType.Should().Be(XLDataType.DateTime, "con fecha sigue siendo una fecha de Excel, ordenable y filtrable");
        hoja.Cell(2, 5).GetDateTime().Should().Be(new DateTime(2027, 3, 1));

        hoja.Cell(3, 2).GetString().Should().Be("Bea Sin Confirmar");
        hoja.Cell(3, 5).DataType.Should().Be(XLDataType.Text);
        hoja.Cell(3, 5).GetString().Should().Be("Sin confirmar");

        hoja.Cell(4, 2).GetString().Should().Be("Carlos Sin Caducidad");
        hoja.Cell(4, 5).GetString().Should().Be("Sin caducidad");

        hoja.Cell(5, 2).GetString().Should().Be("Dora Falta");
        hoja.Cell(5, 5).IsEmpty().Should().BeTrue("un estado sin fecha que no es «Sin caducidad» ni «Sin confirmar» no tiene vigencia que rotular");
    }
}
