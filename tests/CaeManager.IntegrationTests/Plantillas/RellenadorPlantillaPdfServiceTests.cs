using System.Text;
using CaeManager.Application.Common;
using CaeManager.Domain.Plantillas;
using CaeManager.Infrastructure.Plantillas;
using CaeManager.Web.Reportes;
using FluentAssertions;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace CaeManager.IntegrationTests.Plantillas;

public class RellenadorPlantillaPdfServiceTests
{
    private static readonly byte[] TrazoPngDePrueba = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    static RellenadorPlantillaPdfServiceTests()
    {
        GlobalFontSettings.FontResolver ??= new EmbeddedFontResolver();
    }

    private static byte[] CrearPdfVisualDeUnaPagina()
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

    /// <summary>
    /// PdfSharp 6.2.4 no ofrece una API pública para crear campos AcroForm
    /// nuevos (solo para leer/rellenar los que ya trae un PDF) — se
    /// construye a mano el PDF mínimo con un campo de texto, siguiendo la
    /// sintaxis PDF estándar (catálogo → páginas → página → anotación
    /// Widget /FT /Tx → AcroForm), verificado por separado que PdfSharp lo
    /// abre, rellena y guarda sin problemas.
    /// </summary>
    private static byte[] CrearPdfConCampoAcroForm(string nombreCampo)
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
        AppendObj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Annots [5 0 R] >>");
        AppendObj(4, "<< /Length 0 >>\nstream\n\nendstream");
        AppendObj(5, $"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({nombreCampo}) /Rect [100 700 300 720] /F 4 /Parent 6 0 R >>");
        AppendObj(6, "<< /Fields [5 0 R] >>");

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

    [Fact]
    public void RellenarAcroForm_escribe_el_valor_en_el_campo_por_nombre()
    {
        var servicio = new RellenadorPlantillaPdfService();
        var original = CrearPdfConCampoAcroForm("Campo1");
        var elementos = new[]
        {
            new ElementoRellenoPlantilla(TipoElementoPlantilla.Texto, "Campo1", 1, 0, 0, 0, 0, "Juan Pérez")
        };

        var resultado = servicio.Rellenar(original, FormatoOrigenPlantilla.PdfConCampos, elementos);

        using var flujo = new MemoryStream(resultado);
        using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Modify);
        var campo = documento.AcroForm!.Fields["Campo1"] as PdfSharp.Pdf.AcroForms.PdfTextField;
        campo!.Text.Should().Be("Juan Pérez");
    }

    [Fact]
    public void RellenarAcroForm_ignora_elementos_sin_campo_correspondiente()
    {
        var servicio = new RellenadorPlantillaPdfService();
        var original = CrearPdfConCampoAcroForm("Campo1");
        var elementos = new[]
        {
            new ElementoRellenoPlantilla(TipoElementoPlantilla.Texto, "CampoQueNoExiste", 1, 0, 0, 0, 0, "Valor")
        };

        var accion = () => servicio.Rellenar(original, FormatoOrigenPlantilla.PdfConCampos, elementos);

        accion.Should().NotThrow();
    }

    [Fact]
    public void RellenarAcroForm_sin_acroform_en_el_pdf_lanza_excepcion()
    {
        var servicio = new RellenadorPlantillaPdfService();
        var original = CrearPdfVisualDeUnaPagina();
        var elementos = new[]
        {
            new ElementoRellenoPlantilla(TipoElementoPlantilla.Texto, "Campo1", 1, 0, 0, 0, 0, "Valor")
        };

        var accion = () => servicio.Rellenar(original, FormatoOrigenPlantilla.PdfConCampos, elementos);

        accion.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RellenarAcroForm_salta_los_elementos_de_tipo_firma()
    {
        var servicio = new RellenadorPlantillaPdfService();
        var original = CrearPdfConCampoAcroForm("Campo1");
        var elementos = new[]
        {
            new ElementoRellenoPlantilla(TipoElementoPlantilla.Firma, "Campo1", 1, 0, 0, 0, 0, "No debería escribirse")
        };

        var resultado = servicio.Rellenar(original, FormatoOrigenPlantilla.PdfConCampos, elementos);

        using var flujo = new MemoryStream(resultado);
        using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Modify);
        var campo = documento.AcroForm!.Fields["Campo1"] as PdfSharp.Pdf.AcroForms.PdfTextField;
        campo!.Text.Should().BeEmpty();
    }

    [Fact]
    public void RellenarPorPosicion_no_añade_paginas_nuevas()
    {
        var servicio = new RellenadorPlantillaPdfService();
        var original = CrearPdfVisualDeUnaPagina();
        var elementos = new[]
        {
            new ElementoRellenoPlantilla(TipoElementoPlantilla.Texto, null, 1, 50, 700, 200, 20, "Juan Pérez")
        };

        var resultado = servicio.Rellenar(original, FormatoOrigenPlantilla.PdfVisual, elementos);

        ContarPaginas(resultado).Should().Be(ContarPaginas(original));
    }

    [Fact]
    public void RellenarPorPosicion_produce_un_contenido_distinto_del_original()
    {
        var servicio = new RellenadorPlantillaPdfService();
        var original = CrearPdfVisualDeUnaPagina();
        var elementos = new[]
        {
            new ElementoRellenoPlantilla(TipoElementoPlantilla.Texto, null, 1, 50, 700, 200, 20, "Juan Pérez")
        };

        var resultado = servicio.Rellenar(original, FormatoOrigenPlantilla.PdfVisual, elementos);

        resultado.Should().NotBeEquivalentTo(original);
    }

    [Fact]
    public void RellenarPorPosicion_ignora_paginas_fuera_de_rango_sin_fallar()
    {
        var servicio = new RellenadorPlantillaPdfService();
        var original = CrearPdfVisualDeUnaPagina();
        var elementos = new[]
        {
            new ElementoRellenoPlantilla(TipoElementoPlantilla.Texto, null, 99, 50, 700, 200, 20, "Juan Pérez")
        };

        var accion = () => servicio.Rellenar(original, FormatoOrigenPlantilla.PdfVisual, elementos);

        accion.Should().NotThrow();
    }

    [Fact]
    public void RellenarPorPosicion_salta_los_elementos_de_tipo_firma()
    {
        var servicio = new RellenadorPlantillaPdfService();
        var original = CrearPdfVisualDeUnaPagina();
        var elementos = new[]
        {
            new ElementoRellenoPlantilla(TipoElementoPlantilla.Firma, null, 1, 50, 700, 200, 20, "No debería dibujarse", TrazoPngDePrueba)
        };

        var accion = () => servicio.Rellenar(original, FormatoOrigenPlantilla.PdfVisual, elementos);

        accion.Should().NotThrow();
    }

    [Fact]
    public void RellenarPorPosicion_con_imagen_dibuja_sin_fallar()
    {
        var servicio = new RellenadorPlantillaPdfService();
        var original = CrearPdfVisualDeUnaPagina();
        var elementos = new[]
        {
            new ElementoRellenoPlantilla(TipoElementoPlantilla.Imagen, null, 1, 50, 700, 40, 40, null, TrazoPngDePrueba)
        };

        var accion = () => servicio.Rellenar(original, FormatoOrigenPlantilla.PdfVisual, elementos);

        accion.Should().NotThrow();
    }

    [Fact]
    public void Rellenar_con_formato_no_soportado_lanza_excepcion()
    {
        var servicio = new RellenadorPlantillaPdfService();
        var original = CrearPdfVisualDeUnaPagina();
        var elementos = Array.Empty<ElementoRellenoPlantilla>();

        var accion = () => servicio.Rellenar(original, (FormatoOrigenPlantilla)999, elementos);

        accion.Should().Throw<NotSupportedException>();
    }
}
