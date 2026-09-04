using CaeManager.Application.Common;
using CaeManager.Web.Documentos;
using FluentAssertions;
using PdfSharp.Pdf;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// UnificarAsync copiaba las páginas de cada PDF de entrada sin mirar
/// cuántas declaraba. Mismo vector que la bomba de píxeles de
/// <see cref="DimensionesImagenTests"/>, pero para el número de páginas en
/// vez de para las dimensiones de una imagen: un árbol de páginas compacto
/// puede declarar decenas de miles de páginas casi vacías muy por debajo del
/// tope de 10 MB de la subida, y ni ese tope ni el presupuesto del lote (que
/// cuentan bytes de fichero) lo detectan.
///
/// REC-176 (más abajo): esa guarda existía pero llegaba tarde -- para
/// leer PageCount hacía falta que PdfSharp ya hubiera abierto y parseado
/// el árbol de páginas completo, así que el coste que motivaba la guarda
/// ya se había pagado antes de que pudiera actuar. Los tests de esa
/// sección comprueban el MOMENTO del rechazo, no solo que ocurra -- ver su
/// comentario para la trampa de un test que pasaría igual sin el arreglo.
/// </summary>
public class ConversorArchivosPdfTests
{
    /// <summary>PDF con el número de páginas pedido, todas en blanco — el coste que se mide es el de copiarlas, no el de dibujar nada en ellas.</summary>
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
    public async Task Un_pdf_que_declara_demasiadas_paginas_se_rechaza()
    {
        var bomba = CrearPdfConPaginas(2001);

        var convertir = async () => await ConversorArchivosPdf.UnificarAsync(
            [(bomba, "bomba.pdf")], new ConversorWordFalso());

        await convertir.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Varios_pdfs_moderados_que_suman_demasiadas_paginas_tambien_se_rechazan()
    {
        // No basta con mirar cada archivo por separado: el límite es sobre
        // el combinado completo, no por archivo.
        (byte[] Contenido, string NombreArchivo)[] archivos =
        [
            (CrearPdfConPaginas(1200), "parte1.pdf"),
            (CrearPdfConPaginas(1200), "parte2.pdf"),
        ];

        var convertir = async () => await ConversorArchivosPdf.UnificarAsync(archivos, new ConversorWordFalso());

        await convertir.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Un_pdf_de_tamano_normal_se_sigue_combinando()
    {
        // Control positivo: sin él, un guardia que rechazara todo también
        // pasaría el caso de arriba.
        var normal = CrearPdfConPaginas(5);

        var pdf = await ConversorArchivosPdf.UnificarAsync([(normal, "documento.pdf")], new ConversorWordFalso());

        System.Text.Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
    }

    // ── REC-176: el rechazo tiene que ocurrir SIN parsear el árbol de
    // páginas completo, no solo "rechazar" ────────────────────────────────
    //
    // La trampa (documentada en el handoff de REC-176): un test que solo
    // comprueba que un PDF de más páginas se rechaza PASA IGUAL con el
    // código de antes de este incremento — la guarda tras PdfReader.Open ya
    // rechazaba, solo que tarde. Los dos tests de abajo no miran el
    // resultado (ya está cubierto arriba): miran que el rechazo ocurra sin
    // que PdfSharp haya llegado a abrir el documento.

    /// <summary>
    /// IntentarLeerRecuentoDePaginasSinAbrir en aislamiento total: no
    /// intercambia nada con PdfSharp, así que si esto da el número correcto
    /// para un PDF real generado por PdfSharp, es imposible que lo haya
    /// sacado abriendo el documento — el método ni siquiera importa
    /// PdfReader.
    /// </summary>
    [Fact]
    public void IntentarLeerRecuentoDePaginasSinAbrir_lee_el_recuento_real_de_un_pdf_de_pdfsharp()
    {
        var pdf = CrearPdfConPaginas(2500);

        var recuento = ConversorArchivosPdf.IntentarLeerRecuentoDePaginasSinAbrir(pdf);

        recuento.Should().Be(2500);
    }

    [Fact]
    public void IntentarLeerRecuentoDePaginasSinAbrir_no_confunde_un_pdf_moderado_con_uno_que_excede()
    {
        // Control positivo del anterior: si esto diera cualquier número
        // (o ninguno) para un PDF normal, el rechazo de más abajo por este
        // camino sería un accidente, no una lectura real de /Count.
        var pdf = CrearPdfConPaginas(5);

        ConversorArchivosPdf.IntentarLeerRecuentoDePaginasSinAbrir(pdf).Should().Be(5);
    }

    [Fact]
    public void IntentarLeerRecuentoDePaginasSinAbrir_se_abstiene_sin_trailer_literal()
    {
        // Forma de un PDF con xref STREAM (PDF 1.5+): no lleva la palabra
        // "trailer" en absoluto. El método tiene que devolver null -- no
        // inventar un cero ni un número cualquiera -- para que
        // UnificarAsync caiga al camino de siempre (abrir y mirar
        // PageCount), que es la red de seguridad para esta forma.
        var sinTrailer = System.Text.Encoding.Latin1.GetBytes(
            "%PDF-1.7\n1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n" +
            "2 0 obj\n<</Type/Pages/Count 50000/Kids[]>>\nendobj\n" +
            "3 0 obj\n<</Type/XRef/Size 4/Root 1 0 R/W[1 1 1]>>stream\nBASURA\nendstream\nendobj\n" +
            "startxref\n9\n%%EOF");

        ConversorArchivosPdf.IntentarLeerRecuentoDePaginasSinAbrir(sinTrailer).Should().BeNull();
    }

    [Fact]
    public void IntentarLeerRecuentoDePaginasSinAbrir_usa_el_ultimo_trailer_de_dos_actualizaciones_incrementales()
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

        ConversorArchivosPdf.IntentarLeerRecuentoDePaginasSinAbrir(incremental).Should().Be(500_000);
    }

    // ── Hallazgos de la revisión adversarial (agente en este mismo chat,
    // ver el RETURN PACKAGE) ────────────────────────────────────────────

    [Fact]
    public void IntentarLeerRecuentoDePaginasSinAbrir_no_confunde_una_clave_que_es_prefijo_de_otra()
    {
        // Hallazgo del revisor: buscar "/Pages" con IndexOf + una regex sin
        // límite de nombre podía casar con "/PagesBackup" (que CONTIENE
        // "/Pages" como subcadena) en vez de con la clave real "/Pages" que
        // viene después -- leyendo la referencia equivocada. El objeto 10
        // existe adrede y también parece un árbol de páginas válido, para
        // que un match erróneo no se note por casualidad (no encontrar
        // "10 0 obj" y abstenerse).
        var conClavePrefijo = System.Text.Encoding.Latin1.GetBytes(
            "%PDF-1.4\n" +
            "1 0 obj\n<</Type/Catalog/PagesBackup 10 0 R/Pages 2 0 R>>\nendobj\n" +
            "2 0 obj\n<</Type/Pages/Count 7/Kids[]>>\nendobj\n" +
            "10 0 obj\n<</Type/Pages/Count 999999/Kids[]>>\nendobj\n" +
            "trailer\n<</Root 1 0 R/Size 11>>\n" +
            "%%EOF");

        // La clave real es "/Pages" (7 páginas), no "/PagesBackup" (999999).
        ConversorArchivosPdf.IntentarLeerRecuentoDePaginasSinAbrir(conClavePrefijo).Should().Be(7);
    }

    [Fact]
    public void IntentarLeerRecuentoDePaginasSinAbrir_se_abstiene_ante_un_numero_de_objeto_que_no_cabe_en_un_entero()
    {
        // Hallazgo del revisor: el número/generación de "/Root N G R" se
        // parseaba con int.Parse, así que un número de objeto de 11 dígitos
        // (más allá de int.MaxValue) lanzaba OverflowException sin capturar
        // -- justo lo que el contrato de la función promete no hacer nunca
        // sobre bytes de una subida. Once nueves no caben en un Int32.
        //
        // MEDIDO, no solo esperado: revertir SOLO el long.TryParse de
        // BuscarReferencia a int.Parse (dejando intacto el try/catch
        // general del método) NO pone este test en rojo -- el catch de
        // fuera absorbe la misma OverflowException y el resultado
        // observable (abstención) es idéntico. Este test demuestra el
        // CONTRATO final (nunca lanza, se abstiene), no aísla esa línea en
        // particular: las dos capas se solapan a propósito. La única
        // mutación que sí lo pone en rojo por el motivo nombrado es
        // eliminar TAMBIÉN el try/catch de IntentarLeerRecuentoDePaginasSinAbrir.
        // El test que sí aísla el lookahead de BuscarReferencia en
        // solitario es el de arriba (prefijo de clave).
        var numeroDeObjetoDesmesurado = System.Text.Encoding.Latin1.GetBytes(
            "%PDF-1.4\n" +
            "trailer\n<</Root 99999999999 0 R/Size 1>>\n" +
            "%%EOF");

        var actuar = () => ConversorArchivosPdf.IntentarLeerRecuentoDePaginasSinAbrir(numeroDeObjetoDesmesurado);

        actuar.Should().NotThrow();
        actuar().Should().BeNull(); // no puede resolver "99999999999 0 obj": no existe tal objeto -- abstención, no acierto de casualidad.
    }

    /// <summary>
    /// La prueba de integración que observa el MOMENTO, no el resultado.
    ///
    /// Este PDF declara trailer→Root→Pages→Count en texto plano (999 999
    /// páginas), así que el pre-escaneo lo detecta y rechaza. Pero el resto
    /// del fichero es basura deliberada: no hay tabla xref válida ni forma
    /// reconocible más allá de esos dos objetos. Medido con un probe
    /// standalone contra PdfSharp 6.2.4: <c>PdfReader.Open</c> sobre estos
    /// bytes exactos lanza <c>PdfSharp.Pdf.IO.PdfReaderException</c>
    /// ("Unexpected token 'endobj'") para los cuatro <see
    /// cref="PdfSharp.Pdf.IO.PdfDocumentOpenMode"/> públicos -- PdfSharp
    /// jamás llega a devolver un PageCount para este documento.
    ///
    /// Si el rechazo viniera de la comprobación de siempre (después de
    /// abrir), esta llamada no terminaría en <see
    /// cref="InvalidDataException"/> con el mensaje del tope: terminaría en
    /// <c>PdfReaderException</c>, porque PdfSharp no puede abrir estos
    /// bytes. Que el test vea <see cref="InvalidDataException"/> con el
    /// mensaje esperado es la prueba de que el rechazo ocurrió ANTES de
    /// invocar <c>PdfReader.Open</c> -- no un efecto colateral de que
    /// "de todos modos habría fallado".
    /// </summary>
    [Fact]
    public async Task El_rechazo_ocurre_sin_que_PdfSharp_llegue_a_abrir_el_documento()
    {
        var bombaIlegible = System.Text.Encoding.Latin1.GetBytes(
            "%PDF-1.4\n" +
            "1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n" +
            "2 0 obj\n<</Type/Pages/Count 999999/Kids[]>>\nendobj\n" +
            "AQUI NO HAY TABLA XREF VALIDA, SOLO BASURA DELIBERADA\n" +
            "trailer\n<</Root 1 0 R/Size 3>>\n" +
            "startxref\n0\n%%EOF");

        var convertir = async () => await ConversorArchivosPdf.UnificarAsync(
            [(bombaIlegible, "bomba.pdf")], new ConversorWordFalso());

        // Si PdfSharp hubiera llegado a intentar abrir este documento,
        // habría lanzado PdfReaderException (medido), no InvalidDataException.
        var excepcion = await convertir.Should().ThrowAsync<InvalidDataException>();
        excepcion.Which.Message.Should().Contain("2000 páginas");
    }

    private sealed class ConversorWordFalso : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
