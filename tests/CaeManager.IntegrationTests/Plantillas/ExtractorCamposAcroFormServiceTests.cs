using System.Text;
using CaeManager.Infrastructure.Plantillas;
using CaeManager.Web.Reportes;
using FluentAssertions;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Xunit;

namespace CaeManager.IntegrationTests.Plantillas;

/// <summary>
/// Mismo enfoque que <see cref="RellenadorPlantillaPdfServiceTests"/>: PdfSharp
/// 6.2.4 no ofrece una API pública para crear campos AcroForm, así que los PDF
/// de prueba se construyen a mano con la sintaxis PDF mínima necesaria.
/// </summary>
public class ExtractorCamposAcroFormServiceTests
{
    static ExtractorCamposAcroFormServiceTests()
    {
        GlobalFontSettings.FontResolver ??= new EmbeddedFontResolver();
    }

    private const int AlturaPagina = 792;

    private static byte[] ConstruirPdfConCampos((string Nombre, int Pagina, double X1, double Y1, double X2, double Y2)[] campos)
    {
        var totalPaginas = campos.Length == 0 ? 1 : campos.Max(c => c.Pagina);
        var offsets = new List<int>();
        var sb = new StringBuilder();

        void AppendObj(int numero, string cuerpo)
        {
            offsets.Add(sb.Length);
            sb.Append(numero).Append(" 0 obj\n").Append(cuerpo).Append("\nendobj\n");
        }

        sb.Append("%PDF-1.4\n");
        offsets.Add(0);

        // Numeración: 1=Catalog, 2=Pages, 3..3+N-1=páginas, luego campos, luego AcroForm.
        var primeraPaginaObj = 3;
        var primerCampoObj = primeraPaginaObj + totalPaginas;
        var acroFormObj = primerCampoObj + campos.Length;

        var kidsPaginas = string.Join(' ', Enumerable.Range(0, totalPaginas).Select(i => $"{primeraPaginaObj + i} 0 R"));
        AppendObj(1, $"<< /Type /Catalog /Pages 2 0 R /AcroForm {acroFormObj} 0 R >>");
        AppendObj(2, $"<< /Type /Pages /Kids [{kidsPaginas}] /Count {totalPaginas} >>");

        for (var p = 0; p < totalPaginas; p++)
        {
            var numeroPagina = p + 1;
            var camposDeEstaPagina = campos
                .Select((c, indice) => (Campo: c, Indice: indice))
                .Where(x => x.Campo.Pagina == numeroPagina)
                .Select(x => $"{primerCampoObj + x.Indice} 0 R");
            var annots = string.Join(' ', camposDeEstaPagina);
            var annotsDict = annots.Length == 0 ? "" : $" /Annots [{annots}]";
            AppendObj(primeraPaginaObj + p, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 {AlturaPagina}] /Resources << >>{annotsDict} >>");
        }

        for (var i = 0; i < campos.Length; i++)
        {
            var c = campos[i];
            AppendObj(primerCampoObj + i,
                $"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({c.Nombre}) /Rect [{Fmt(c.X1)} {Fmt(c.Y1)} {Fmt(c.X2)} {Fmt(c.Y2)}] /F 4 /Parent {acroFormObj} 0 R >>");
        }

        var camposRefs = string.Join(' ', Enumerable.Range(0, campos.Length).Select(i => $"{primerCampoObj + i} 0 R"));
        AppendObj(acroFormObj, $"<< /Fields [{camposRefs}] >>");

        var inicioXref = sb.Length;
        var totalObjetos = acroFormObj + 1;
        sb.Append($"xref\n0 {totalObjetos}\n");
        sb.Append("0000000000 65535 f \n");
        for (var i = 1; i < totalObjetos; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n").Append($"<< /Size {totalObjetos} /Root 1 0 R >>\n")
          .Append("startxref\n").Append(inicioXref).Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Fmt(double valor) => valor.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// PDF hostil: dos nodos /Kids que se referencian mutuamente (4 → 5 → 4 →
    /// ...), sin ningún campo hoja real. Antes de la guarda de
    /// <see cref="RecorridoCamposAcroFormSeguro"/>, seguir /Kids con
    /// recursión directa agota la pila (StackOverflowException, no
    /// capturable) y tumba el proceso completo.
    /// </summary>
    private static byte[] ConstruirPdfConCicloEnKids()
    {
        var offsets = new List<int>();
        var sb = new StringBuilder();

        void AppendObj(int numero, string cuerpo)
        {
            offsets.Add(sb.Length);
            sb.Append(numero).Append(" 0 obj\n").Append(cuerpo).Append("\nendobj\n");
        }

        sb.Append("%PDF-1.4\n");
        offsets.Add(0);

        AppendObj(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 6 0 R >>");
        AppendObj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        AppendObj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>");
        AppendObj(4, "<< /Kids [5 0 R] /Parent 5 0 R >>");
        AppendObj(5, "<< /Kids [4 0 R] /Parent 4 0 R >>");
        AppendObj(6, "<< /Fields [4 0 R] >>");

        var inicioXref = sb.Length;
        const int totalObjetos = 7;
        sb.Append($"xref\n0 {totalObjetos}\n");
        sb.Append("0000000000 65535 f \n");
        for (var i = 1; i < totalObjetos; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n").Append($"<< /Size {totalObjetos} /Root 1 0 R >>\n")
          .Append("startxref\n").Append(inicioXref).Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] CrearPdfSinAcroForm()
    {
        using var documento = new PdfDocument();
        var pagina = documento.AddPage();
        using (var graficos = XGraphics.FromPdfPage(pagina))
            graficos.DrawString("Sin campos", new XFont(EmbeddedFontResolver.NombreFuente, 12), XBrushes.Black, new XPoint(20, 20));
        using var salida = new MemoryStream();
        documento.Save(salida);
        return salida.ToArray();
    }

    [Fact]
    public void Extraer_convierte_el_rect_nativo_de_pdf_a_coordenadas_arriba_izquierda()
    {
        var servicio = new ExtractorCamposAcroFormService();
        var pdf = ConstruirPdfConCampos([("CIF", 1, 100, 600, 300, 620)]);

        var candidatos = servicio.Extraer(pdf);

        var campo = candidatos.Should().ContainSingle().Subject;
        campo.NombreCampo.Should().Be("CIF");
        campo.Pagina.Should().Be(1);
        campo.X.Should().Be(100);
        campo.Y.Should().Be(AlturaPagina - 620); // top-left: alturaPagina - Y2
        campo.Ancho.Should().Be(200);
        campo.Alto.Should().Be(20);
    }

    [Fact]
    public void Extraer_asigna_cada_campo_a_su_pagina_correcta()
    {
        var servicio = new ExtractorCamposAcroFormService();
        var pdf = ConstruirPdfConCampos(
        [
            ("CampoPagina1", 1, 100, 600, 300, 620),
            ("CampoPagina2", 2, 50, 700, 250, 730),
        ]);

        var candidatos = servicio.Extraer(pdf);

        candidatos.Should().HaveCount(2);
        candidatos.Single(c => c.NombreCampo == "CampoPagina1").Pagina.Should().Be(1);
        candidatos.Single(c => c.NombreCampo == "CampoPagina2").Pagina.Should().Be(2);
    }

    [Fact]
    public void Extraer_sin_acroform_devuelve_lista_vacia()
    {
        var servicio = new ExtractorCamposAcroFormService();
        var pdf = CrearPdfSinAcroForm();

        var candidatos = servicio.Extraer(pdf);

        candidatos.Should().BeEmpty();
    }

    [Fact]
    public void Extraer_sin_campos_devuelve_lista_vacia()
    {
        var servicio = new ExtractorCamposAcroFormService();
        var pdf = ConstruirPdfConCampos([]);

        var candidatos = servicio.Extraer(pdf);

        candidatos.Should().BeEmpty();
    }

    [Fact]
    public void Extraer_con_ciclo_en_Kids_no_cuelga_ni_lanza_stack_overflow()
    {
        var servicio = new ExtractorCamposAcroFormService();
        var pdf = ConstruirPdfConCicloEnKids();

        var candidatos = servicio.Extraer(pdf);

        candidatos.Should().BeEmpty();
    }
}
