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

    /// <summary>
    /// PDF con una casilla y un grupo de radio con dos opciones, cada widget
    /// con sus apariencias declaradas en <c>/AP /N</c> — sin ellas no hay nada
    /// que comprobar: <c>/AS</c> solo tiene sentido contra los estados que el
    /// propio PDF declara (M4 § 3.2).
    /// </summary>
    private static byte[] CrearPdfConCasillaYRadio()
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

        AppendObj(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 9 0 R >>");
        AppendObj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        AppendObj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Annots [5 0 R 6 0 R 7 0 R] >>");
        AppendObj(4, "<< /Length 0 >>\nstream\n\nendstream");
        AppendObj(5, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (Casilla1) /Rect [100 700 120 720] /F 4 /V /Off /AS /Off /AP << /N << /Off 10 0 R /Yes 11 0 R >> >> >>");
        AppendObj(6, "<< /Type /Annot /Subtype /Widget /Parent 8 0 R /Rect [100 650 120 670] /F 4 /AS /Off /AP << /N << /Off 10 0 R /Op1 11 0 R >> >> >>");
        AppendObj(7, "<< /Type /Annot /Subtype /Widget /Parent 8 0 R /Rect [130 650 150 670] /F 4 /AS /Off /AP << /N << /Off 10 0 R /Op2 11 0 R >> >> >>");
        AppendObj(8, "<< /FT /Btn /Ff 32768 /T (Radio1) /V /Off /Kids [6 0 R 7 0 R] >>");
        AppendObj(9, "<< /Fields [5 0 R 8 0 R] >>");
        AppendObj(10, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length 0 >>\nstream\n\nendstream");
        AppendObj(11, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length 22 >>\nstream\n0 0 1 rg 0 0 20 20 re f\nendstream");

        var inicioXref = sb.Length;
        const int totalObjetos = 12;
        sb.Append($"xref\n0 {totalObjetos}\n");
        sb.Append("0000000000 65535 f \n");
        for (var i = 1; i < totalObjetos; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n").Append($"<< /Size {totalObjetos} /Root 1 0 R >>\n")
          .Append("startxref\n").Append(inicioXref).Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Lee el diccionario crudo del campo tras releer el PDF — no la propiedad
    /// tipada: lo que un visor mira es <c>/V</c> y <c>/AS</c>, no lo que PdfSharp
    /// deduzca. Devuelve la forma textual del elemento en vez de exigir un
    /// nombre (<c>GetName</c> lanza si no lo es): así un valor escrito como
    /// cadena —el defecto que estos tests vigilan— sale en el mensaje del fallo
    /// como <c>(Yes)</c> frente a <c>/Yes</c>, en lugar de reventar la lectura
    /// antes de llegar a comprobar <c>/AS</c>.
    /// </summary>
    private static string? LeerClave(PdfSharp.Pdf.PdfDictionary? diccionario, string clave) =>
        diccionario?.Elements.GetValue(clave)?.ToString();

    [Fact]
    public void RellenarAcroForm_marca_la_casilla_con_V_y_AS_coherentes()
    {
        var servicio = new RellenadorPlantillaPdfService();
        var original = CrearPdfConCasillaYRadio();
        var elementos = new[]
        {
            new ElementoRellenoPlantilla(TipoElementoPlantilla.Checkbox, "Casilla1", 1, 0, 0, 0, 0, "true")
        };

        var resultado = servicio.Rellenar(original, FormatoOrigenPlantilla.PdfConCampos, elementos);

        using var flujo = new MemoryStream(resultado);
        using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Modify);
        var casilla = documento.AcroForm!.Fields["Casilla1"];
        LeerClave(casilla, "/V").Should().Be("/Yes", "el valor debe ser el nombre de estado que declara el propio PDF");
        LeerClave(casilla, "/AS").Should().Be("/Yes", "sin /AS el visor sigue dibujando la casilla sin marcar");
    }

    [Fact]
    public void RellenarAcroForm_deja_la_casilla_apagada_cuando_el_valor_es_negativo()
    {
        var servicio = new RellenadorPlantillaPdfService();
        var original = CrearPdfConCasillaYRadio();
        var elementos = new[]
        {
            new ElementoRellenoPlantilla(TipoElementoPlantilla.Checkbox, "Casilla1", 1, 0, 0, 0, 0, "no")
        };

        var resultado = servicio.Rellenar(original, FormatoOrigenPlantilla.PdfConCampos, elementos);

        using var flujo = new MemoryStream(resultado);
        using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Modify);
        var casilla = documento.AcroForm!.Fields["Casilla1"];
        LeerClave(casilla, "/V").Should().Be("/Off");
        LeerClave(casilla, "/AS").Should().Be("/Off");
    }

    [Fact]
    public void RellenarAcroForm_selecciona_la_opcion_del_radio_y_apaga_las_demas()
    {
        var servicio = new RellenadorPlantillaPdfService();
        var original = CrearPdfConCasillaYRadio();
        var elementos = new[]
        {
            new ElementoRellenoPlantilla(TipoElementoPlantilla.Texto, "Radio1", 1, 0, 0, 0, 0, "Op2")
        };

        var resultado = servicio.Rellenar(original, FormatoOrigenPlantilla.PdfConCampos, elementos);

        using var flujo = new MemoryStream(resultado);
        using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Modify);
        var grupo = documento.AcroForm!.Fields["Radio1"]!;
        LeerClave(grupo, "/V").Should().Be("/Op2");
        LeerClave(grupo.Fields[0], "/AS").Should().Be("/Off");
        LeerClave(grupo.Fields[1], "/AS").Should().Be("/Op2");
    }

    [Fact]
    public void RellenarAcroForm_activa_NeedAppearances_como_red_de_seguridad()
    {
        var servicio = new RellenadorPlantillaPdfService();
        var original = CrearPdfConCasillaYRadio();
        var elementos = new[]
        {
            new ElementoRellenoPlantilla(TipoElementoPlantilla.Checkbox, "Casilla1", 1, 0, 0, 0, 0, "true")
        };

        var resultado = servicio.Rellenar(original, FormatoOrigenPlantilla.PdfConCampos, elementos);

        using var flujo = new MemoryStream(resultado);
        using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Modify);
        documento.AcroForm!.Elements.GetBoolean("/NeedAppearances").Should().BeTrue();
    }

    /// <summary>
    /// Grupo de radio con valores exportados en <c>/Opt</c> y nombres de estado
    /// que son índices (<c>/0</c>, <c>/1</c>) — la forma habitual cuando dos
    /// opciones comparten etiqueta. Sin leer <c>/Opt</c> ningún valor casa y el
    /// grupo entero se queda en <c>/Off</c>.
    /// </summary>
    private static byte[] CrearPdfConRadioConOpt()
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

        AppendObj(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 8 0 R >>");
        AppendObj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        AppendObj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Annots [5 0 R 6 0 R] >>");
        AppendObj(4, "<< /Length 0 >>\nstream\n\nendstream");
        AppendObj(5, "<< /Type /Annot /Subtype /Widget /Parent 7 0 R /Rect [100 650 120 670] /F 4 /AS /Off /AP << /N << /Off 9 0 R /0 10 0 R >> >> >>");
        AppendObj(6, "<< /Type /Annot /Subtype /Widget /Parent 7 0 R /Rect [130 650 150 670] /F 4 /AS /Off /AP << /N << /Off 9 0 R /1 10 0 R >> >> >>");
        AppendObj(7, "<< /FT /Btn /Ff 32768 /T (Apto) /V /Off /Opt [(Apto) (No apto)] /Kids [5 0 R 6 0 R] >>");
        AppendObj(8, "<< /Fields [7 0 R] >>");
        AppendObj(9, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length 0 >>\nstream\n\nendstream");
        AppendObj(10, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length 22 >>\nstream\n0 0 1 rg 0 0 20 20 re f\nendstream");

        var inicioXref = sb.Length;
        const int totalObjetos = 11;
        sb.Append($"xref\n0 {totalObjetos}\n");
        sb.Append("0000000000 65535 f \n");
        for (var i = 1; i < totalObjetos; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n").Append($"<< /Size {totalObjetos} /Root 1 0 R >>\n")
          .Append("startxref\n").Append(inicioXref).Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void RellenarAcroForm_selecciona_la_opcion_de_un_radio_con_Opt_por_su_valor_exportado()
    {
        var servicio = new RellenadorPlantillaPdfService();
        var original = CrearPdfConRadioConOpt();
        var elementos = new[]
        {
            new ElementoRellenoPlantilla(TipoElementoPlantilla.Texto, "Apto", 1, 0, 0, 0, 0, "No apto")
        };

        var resultado = servicio.Rellenar(original, FormatoOrigenPlantilla.PdfConCampos, elementos);

        using var flujo = new MemoryStream(resultado);
        using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Modify);
        var grupo = documento.AcroForm!.Fields["Apto"]!;
        LeerClave(grupo, "/V").Should().Be("/1", "el valor exportado (Opt[1]) selecciona el hijo 1, cuyo estado se llama /1");
        LeerClave(grupo.Fields[0], "/AS").Should().Be("/Off");
        LeerClave(grupo.Fields[1], "/AS").Should().Be("/1");
    }

    /// <summary>
    /// Semántica única de "marcado" entre los dos motores: los espacios no
    /// convierten un negativo en positivo. Sin el Trim de EsValorAfirmativo,
    /// " false " no casaba con "false" y la casilla salía marcada.
    /// </summary>
    [Fact]
    public void RellenarAcroForm_no_marca_la_casilla_con_un_negativo_rodeado_de_espacios()
    {
        var servicio = new RellenadorPlantillaPdfService();
        var original = CrearPdfConCasillaYRadio();
        var elementos = new[]
        {
            new ElementoRellenoPlantilla(TipoElementoPlantilla.Checkbox, "Casilla1", 1, 0, 0, 0, 0, "  false  ")
        };

        var resultado = servicio.Rellenar(original, FormatoOrigenPlantilla.PdfConCampos, elementos);

        using var flujo = new MemoryStream(resultado);
        using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Modify);
        var casilla = documento.AcroForm!.Fields["Casilla1"];
        LeerClave(casilla, "/V").Should().Be("/Off");
        LeerClave(casilla, "/AS").Should().Be("/Off");
    }

    [Fact]
    public void RellenarAcroForm_con_ciclo_en_Kids_no_cuelga_ni_lanza_stack_overflow()
    {
        var servicio = new RellenadorPlantillaPdfService();
        var original = ConstruirPdfConCicloEnKids();

        var accion = () => servicio.Rellenar(original, FormatoOrigenPlantilla.PdfConCampos, []);

        accion.Should().NotThrow();
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

    /// <summary>
    /// Auditoría de seguridad del módulo (2026-08-30), pendiente 3.2: antes
    /// este caso se descartaba en silencio (el documento salía igual, con el
    /// campo en blanco) — ahora falla con la lista de campos que no existen,
    /// para que nunca llegue a generarse un documento incompleto sin aviso.
    /// </summary>
    [Fact]
    public void RellenarAcroForm_con_campo_sin_correspondencia_lanza_excepcion_con_el_nombre()
    {
        var servicio = new RellenadorPlantillaPdfService();
        var original = CrearPdfConCampoAcroForm("Campo1");
        var elementos = new[]
        {
            new ElementoRellenoPlantilla(TipoElementoPlantilla.Texto, "CampoQueNoExiste", 1, 0, 0, 0, 0, "Valor")
        };

        var accion = () => servicio.Rellenar(original, FormatoOrigenPlantilla.PdfConCampos, elementos);

        accion.Should().Throw<CamposAcroFormFaltantesException>()
            .Which.CamposFaltantes.Should().ContainSingle().Which.Should().Be("CampoQueNoExiste");
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
