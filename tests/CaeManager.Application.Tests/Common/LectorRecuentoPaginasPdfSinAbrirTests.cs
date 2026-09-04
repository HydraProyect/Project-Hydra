using CaeManager.Application.Common;
using FluentAssertions;
using PdfSharp.Pdf;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Cobertura del gemelo de Application (REC-186) del pre-escaneo de
/// ConversorArchivosPdf (REC-176) — ver el doc-comment de
/// <see cref="LectorRecuentoPaginasPdfSinAbrir"/> para por qué es un gemelo
/// deliberado y no código compartido. Misma batería de casos límite que
/// ConversorArchivosPdfTests, porque es la misma lógica exacta duplicada:
/// lectura real de /Count, abstención ante formas no cubiertas, último
/// trailer de varias actualizaciones incrementales, y los dos hallazgos de
/// la revisión adversarial de REC-176 (clave que es prefijo de otra, número
/// de objeto que no cabe en un entero).
/// </summary>
public class LectorRecuentoPaginasPdfSinAbrirTests
{
    private static byte[] CrearPdfConPaginas(int numeroPaginas)
    {
        using var documento = new PdfDocument();
        for (var i = 0; i < numeroPaginas; i++)
            documento.AddPage();

        using var salida = new MemoryStream();
        documento.Save(salida);
        return salida.ToArray();
    }

    [Fact]
    public void Lee_el_recuento_real_de_un_pdf_de_pdfsharp()
    {
        var pdf = CrearPdfConPaginas(2500);

        LectorRecuentoPaginasPdfSinAbrir.IntentarLeerRecuentoDePaginasSinAbrir(pdf).Should().Be(2500);
    }

    [Fact]
    public void No_confunde_un_pdf_moderado_con_uno_que_excede()
    {
        var pdf = CrearPdfConPaginas(5);

        LectorRecuentoPaginasPdfSinAbrir.IntentarLeerRecuentoDePaginasSinAbrir(pdf).Should().Be(5);
    }

    [Fact]
    public void Se_abstiene_sin_trailer_literal()
    {
        // Forma de un PDF con xref STREAM (PDF 1.5+): no lleva la palabra
        // "trailer" en absoluto. Debe devolver null -- no inventar un cero
        // ni un número cualquiera -- para que el sitio que llama caiga al
        // camino de siempre (abrir y mirar PageCount), la red de seguridad
        // para esta forma.
        var sinTrailer = System.Text.Encoding.Latin1.GetBytes(
            "%PDF-1.7\n1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n" +
            "2 0 obj\n<</Type/Pages/Count 50000/Kids[]>>\nendobj\n" +
            "3 0 obj\n<</Type/XRef/Size 4/Root 1 0 R/W[1 1 1]>>stream\nBASURA\nendstream\nendobj\n" +
            "startxref\n9\n%%EOF");

        LectorRecuentoPaginasPdfSinAbrir.IntentarLeerRecuentoDePaginasSinAbrir(sinTrailer).Should().BeNull();
    }

    [Fact]
    public void Usa_el_ultimo_trailer_de_dos_actualizaciones_incrementales()
    {
        // Dos "trailer": el primero es una versión vieja del documento con
        // pocas páginas, el segundo (el vigente) declara muchísimas. Leer
        // el trailer equivocado daría un falso negativo -- aceptaría un
        // documento que en realidad excede el tope.
        var incremental = System.Text.Encoding.Latin1.GetBytes(
            "%PDF-1.4\n" +
            "1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n" +
            "2 0 obj\n<</Type/Pages/Count 3/Kids[]>>\nendobj\n" +
            "trailer\n<</Root 1 0 R/Size 3>>\n" +
            "%%EOF\n" +
            "1 0 obj\n<</Type/Catalog/Pages 4 0 R>>\nendobj\n" +
            "4 0 obj\n<</Type/Pages/Count 500000/Kids[]>>\nendobj\n" +
            "trailer\n<</Root 1 0 R/Size 5>>\n" +
            "%%EOF");

        LectorRecuentoPaginasPdfSinAbrir.IntentarLeerRecuentoDePaginasSinAbrir(incremental).Should().Be(500_000);
    }

    [Fact]
    public void No_confunde_una_clave_que_es_prefijo_de_otra()
    {
        // Buscar "/Pages" con un lookahead flojo podría casar con
        // "/PagesBackup" (que CONTIENE "/Pages" como subcadena) en vez de
        // con la clave real "/Pages". El objeto 10 existe adrede y también
        // parece un árbol de páginas válido, para que un match erróneo no
        // se note por casualidad.
        var conClavePrefijo = System.Text.Encoding.Latin1.GetBytes(
            "%PDF-1.4\n" +
            "1 0 obj\n<</Type/Catalog/PagesBackup 10 0 R/Pages 2 0 R>>\nendobj\n" +
            "2 0 obj\n<</Type/Pages/Count 7/Kids[]>>\nendobj\n" +
            "10 0 obj\n<</Type/Pages/Count 999999/Kids[]>>\nendobj\n" +
            "trailer\n<</Root 1 0 R/Size 11>>\n" +
            "%%EOF");

        LectorRecuentoPaginasPdfSinAbrir.IntentarLeerRecuentoDePaginasSinAbrir(conClavePrefijo).Should().Be(7);
    }

    [Fact]
    public void Se_abstiene_ante_un_numero_de_objeto_que_no_cabe_en_un_entero()
    {
        // Once nueves no caben en un Int32 -- el contrato de esta función
        // es no lanzar nunca sobre bytes no confiables, sino abstenerse.
        var numeroDeObjetoDesmesurado = System.Text.Encoding.Latin1.GetBytes(
            "%PDF-1.4\n" +
            "trailer\n<</Root 99999999999 0 R/Size 1>>\n" +
            "%%EOF");

        var actuar = () => LectorRecuentoPaginasPdfSinAbrir.IntentarLeerRecuentoDePaginasSinAbrir(numeroDeObjetoDesmesurado);

        actuar.Should().NotThrow();
        actuar().Should().BeNull();
    }
}
