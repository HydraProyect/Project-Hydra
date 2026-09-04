using System.Text;
using System.Text.RegularExpressions;
using CaeManager.Application.Common;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace CaeManager.Web.Documentos;

/// <summary>
/// Convierte imágenes JPG/PNG y documentos Word (.docx) a PDF, y combina
/// varios archivos (PDF, imágenes y/o Word, en cualquier orden) en un único
/// PDF multipágina — para el caso común de subir varias fotos de las
/// páginas de un mismo documento. Imágenes usan PdfSharp directamente igual
/// que GeneradorPdfReporteDocumentos (ver esa clase sobre por qué no se usa
/// QuestPDF); Word delega en <see cref="IConversorWordPdfService"/>
/// (LibreOffice headless en Infrastructure) porque no existe una librería
/// .NET pura capaz de renderizar el layout real de un .docx.
/// </summary>
public static class ConversorArchivosPdf
{
    private const double MargenPuntos = 20;

    /// <summary>
    /// Tope de páginas del PDF combinado. Mismo vector que la bomba de
    /// píxeles de <see cref="DimensionesImagen"/>: el coste de combinar un
    /// PDF lo fija su número de páginas declarado, no el tamaño del
    /// fichero en disco — un árbol de páginas compacto puede declarar
    /// decenas de miles de páginas casi vacías muy por debajo del tope de
    /// 10 MB de la subida, y ni ese tope ni el presupuesto del lote (que
    /// cuentan bytes) lo detectan.
    ///
    /// 2000 es generoso para lo que sube gente de verdad —ni el escaneo más
    /// largo de un historial de reconocimientos médicos se acerca—, pero a
    /// diferencia del tope de <see cref="DimensionesImagen"/> (medido: un
    /// PNG de 136 KB con 12000×12000 px hacía reservar 789 MB, factor 5800),
    /// este valor NO está medido contra un coste real de PdfSharp — es una
    /// estimación razonada, no un punto de ruptura comprobado.
    ///
    /// EL DEFECTO DE ORDEN (REC-176) Y LO QUE <see
    /// cref="IntentarLeerRecuentoDePaginasSinAbrir"/> PROTEGE, Y LO QUE NO:
    /// medido (probe standalone, PDFsharp 6.2.4): abrir un PDF de 20 000
    /// páginas en blanco con <c>PdfReader.Open</c> tarda entre 217 y 380 ms
    /// según el <see cref="PdfDocumentOpenMode"/> — los cuatro modos
    /// públicos miden igual porque los cuatro construyen el árbol de
    /// páginas completo, no hay uno más barato en la API pública. Eso
    /// ocurre ANTES de que la comprobación de más abajo pueda actuar: la
    /// guarda original cortaba el coste de <c>AddPage</c> por debajo, no el
    /// de abrir/parsear el árbol de páginas declarado.
    ///
    /// <see cref="IntentarLeerRecuentoDePaginasSinAbrir"/> cierra esa
    /// ventana SOLO para la forma "clásica" de PDF (trailer, Root, Pages y
    /// Count en texto plano, sin xref comprimida ni object streams): ahí
    /// lee el mismo <c>/Count</c> que PdfSharp leería, con el mismo
    /// contrato de PDF, y rechaza sin abrir. Para un PDF cifrado, con xref
    /// stream (PDF 1.5+) o con esos objetos dentro de un object stream
    /// comprimido, ese método se abstiene (null) y la comprobación de más
    /// abajo — sin cambios, tras abrir con <c>PdfReader.Open</c> — sigue
    /// siendo la única red: para esos documentos la ventana de REC-176
    /// sigue abierta exactamente igual que antes. No es una cota
    /// aproximada: es una cota exacta con cobertura parcial y declarada.
    /// </summary>
    private const int MaximoPaginasCombinadas = 2000;

    private static readonly string[] ExtensionesImagen = [".jpg", ".jpeg", ".png"];

    public static bool EsImagen(string nombreArchivo) =>
        ExtensionesImagen.Any(ext => nombreArchivo.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    public static bool EsPdf(string nombreArchivo) =>
        nombreArchivo.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    public static bool EsWord(string nombreArchivo) =>
        nombreArchivo.EndsWith(".docx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Combina uno o varios archivos (PDF, imagen o Word) en un único PDF,
    /// en el orden recibido. Con un solo archivo PDF de entrada, el
    /// resultado es equivalente al original — no hace falta un camino
    /// especial para el caso de un único PDF sin convertir.
    /// </summary>
    public static async Task<byte[]> UnificarAsync(
        IReadOnlyList<(byte[] Contenido, string NombreArchivo)> archivos,
        IConversorWordPdfService conversorWord,
        CancellationToken cancellationToken = default)
    {
        using var documentoFinal = new PdfDocument();

        foreach (var (contenido, nombreArchivo) in archivos)
        {
            var contenidoPdf = EsImagen(nombreArchivo) ? ConvertirImagenAPdf(contenido)
                : EsWord(nombreArchivo) ? await conversorWord.ConvertirAPdfAsync(contenido, cancellationToken)
                : contenido;

            // ANTES de abrir con PdfReader (que parsea el árbol de páginas
            // completo — ver el comentario de MaximoPaginasCombinadas):
            // para la forma "clásica" de PDF esto ya sabe si el combinado
            // se pasaría de tope sin pagar ese coste. Es la misma cuenta
            // sobre el combinado completo, no por archivo. Si no puede
            // determinarlo (documento cifrado, xref comprimida, object
            // streams...) devuelve null y no cambia nada: la comprobación
            // de siempre, más abajo, sigue intacta como red de seguridad.
            var recuentoSinAbrir = IntentarLeerRecuentoDePaginasSinAbrir(contenidoPdf);
            if (recuentoSinAbrir is { } paginasDeclaradas &&
                documentoFinal.PageCount + paginasDeclaradas > MaximoPaginasCombinadas)
                throw new InvalidDataException(
                    $"El documento combinado supera el máximo de {MaximoPaginasCombinadas} páginas admitidas y no se puede procesar.");

            using var flujo = new MemoryStream(contenidoPdf);
            using var documentoOrigen = PdfReader.Open(flujo, PdfDocumentOpenMode.Import);

            // Red de seguridad para todo lo que la comprobación de arriba no
            // pudo cubrir (ver su motivo en el comentario de
            // MaximoPaginasCombinadas): aquí SÍ se ha pagado ya el coste de
            // abrir, pero la decisión de aceptar o rechazar nunca cambia
            // respecto a antes de este incremento.
            if (documentoFinal.PageCount + documentoOrigen.PageCount > MaximoPaginasCombinadas)
                throw new InvalidDataException(
                    $"El documento combinado supera el máximo de {MaximoPaginasCombinadas} páginas admitidas y no se puede procesar.");

            foreach (var pagina in documentoOrigen.Pages)
                documentoFinal.AddPage(pagina);
        }

        using var salida = new MemoryStream();
        documentoFinal.Save(salida);
        return salida.ToArray();
    }

    /// <summary>
    /// Intenta leer cuántas páginas declara este PDF <b>sin</b> invocar
    /// <see cref="PdfReader"/> — sin pagar el coste de que PdfSharp parsee
    /// el árbol de páginas completo (ver el comentario de <see
    /// cref="MaximoPaginasCombinadas"/> para la medición de ese coste).
    ///
    /// Solo cubre la forma "clásica" de PDF: un <c>trailer</c> literal en
    /// texto plano cuyo <c>/Root</c> lleva a un objeto <c>/Catalog</c>
    /// también en texto plano, cuyo <c>/Pages</c> lleva al nodo raíz del
    /// árbol de páginas, también en texto plano, con su <c>/Count</c>
    /// declarado ahí mismo. Por el contrato de PDF (ISO 32000-1 §
    /// 7.7.3.2), el <c>/Count</c> del nodo raíz ya es el total de páginas
    /// hoja de todo el árbol — no hace falta bajar a los <c>/Kids</c> ni
    /// sumar nada, ni recorrer nodos intermedios.
    ///
    /// Si el documento tiene actualizaciones incrementales (varios
    /// <c>trailer</c>), usa el ÚLTIMO — es el vigente — y si el objeto
    /// referenciado se reescribió más adelante en el fichero, también usa
    /// su última aparición textual.
    ///
    /// Cuando el documento no tiene esa forma —cifrado, con flujos de
    /// referencias cruzadas comprimidos (xref stream, PDF 1.5+ — que no
    /// llevan la palabra <c>trailer</c> en absoluto) o con los objetos que
    /// interesan aquí dentro de un object stream comprimido— no hay
    /// <c>trailer</c> literal, o el objeto referenciado no aparece en
    /// texto plano, y este método <b>se abstiene devolviendo null</b> en
    /// vez de adivinar un número. Es una lectura EXACTA para lo que
    /// cubre, y una ABSTENCIÓN para todo lo demás — nunca una
    /// aproximación: la comprobación tras <see cref="PdfReader.Open(Stream,
    /// PdfDocumentOpenMode, PdfReaderOptions)"/> en <see
    /// cref="UnificarAsync"/> sigue intacta como red de seguridad para
    /// cualquier documento que esta función no sepa leer, así que la
    /// decisión de aceptar o rechazar un documento nunca cambia por su
    /// causa — solo cambia CUÁNDO se paga el coste de descubrirlo.
    /// </summary>
    public static long? IntentarLeerRecuentoDePaginasSinAbrir(byte[] contenidoPdf)
    {
        try
        {
            // Latin1: cada byte se conserva 1:1 como char, así el
            // desplazamiento en el string coincide con el desplazamiento en
            // bytes del PDF — no hace falta decodificar el documento de
            // verdad para leer los pocos tokens ASCII de la sintaxis de PDF
            // que interesan aquí.
            var texto = Encoding.Latin1.GetString(contenidoPdf);

            var indiceTrailer = texto.LastIndexOf("trailer", StringComparison.Ordinal);
            if (indiceTrailer < 0) return null;

            if (BuscarReferencia(texto, "/Root", indiceTrailer) is not (long numeroRoot, long generacionRoot))
                return null;

            var cuerpoRoot = BuscarUltimoCuerpoDeObjeto(texto, numeroRoot, generacionRoot);
            if (cuerpoRoot is null) return null;

            if (BuscarReferencia(cuerpoRoot, "/Pages", 0) is not (long numeroPages, long generacionPages))
                return null;

            var cuerpoPages = BuscarUltimoCuerpoDeObjeto(texto, numeroPages, generacionPages);
            if (cuerpoPages is null) return null;
            if (!cuerpoPages.Contains("/Type/Pages", StringComparison.Ordinal) &&
                !cuerpoPages.Contains("/Type /Pages", StringComparison.Ordinal))
                return null;

            var coincidencia = Regex.Match(cuerpoPages, @"/Count\s+(\d+)", RegexOptions.None, TimeoutBusquedaTextual);
            if (!coincidencia.Success) return null;

            // Un /Count que no cabe en long es, para cualquier tope razonable,
            // "se pasa igual" — no hace falta un OverflowException para saberlo.
            return long.TryParse(coincidencia.Groups[1].Value, out var numeroDePaginas)
                ? numeroDePaginas
                : long.MaxValue;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            // Contrato de esta función: SIEMPRE se abstiene (null) en vez de
            // lanzar, sobre bytes que vienen de una subida y no son de
            // fiar. Esto no es "tragarse errores por comodidad": es la
            // frontera explícita entre "no pude determinarlo con
            // seguridad" (null, cae a la comprobación de siempre tras abrir
            // con PdfReader) y cualquier forma en que un texto adversario
            // pueda violar un supuesto de este pre-escaneo (regex con
            // tiempo agotado, índices fuera de rango...) — ambos casos
            // deben terminar igual aquí. Ver
            // IntentarLeerRecuentoDePaginasSinAbrir_se_abstiene_ante_un_numero_de_objeto_que_no_cabe_en_un_entero
            // en ConversorArchivosPdfTests, que fuerza justamente eso.
        }

        return null;
    }

    /// <summary>Tiempo máximo por búsqueda textual — defensa en profundidad sobre bytes no confiables; ningún patrón de aquí necesita de verdad tanto.</summary>
    private static readonly TimeSpan TimeoutBusquedaTextual = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Busca "/etiqueta N G R" a partir de <paramref name="desdeIndice"/> y
    /// devuelve (N, G), o null si no aparece con esa forma. El lookahead
    /// tras la etiqueta exige un delimitador de nombre de PDF (blanco,
    /// paréntesis, corchete, otra barra...) o el final del texto —
    /// si no, la etiqueta buscada sería solo el PREFIJO de un nombre más
    /// largo (p.ej. "/Pages" dentro de "/PagesBackup"), y no la clave real.
    /// </summary>
    private static (long numero, long generacion)? BuscarReferencia(string texto, string etiqueta, int desdeIndice)
    {
        var patron = Regex.Escape(etiqueta) + @"(?=[\s()<>\[\]{}/%]|$)\s+(\d+)\s+(\d+)\s+R";
        var coincidencia = new Regex(patron, RegexOptions.None, TimeoutBusquedaTextual).Match(texto, desdeIndice);
        if (!coincidencia.Success) return null;

        if (!long.TryParse(coincidencia.Groups[1].Value, out var numero)) return null;
        if (!long.TryParse(coincidencia.Groups[2].Value, out var generacion)) return null;
        return (numero, generacion);
    }

    /// <summary>
    /// Busca la ÚLTIMA aparición textual de "N G obj" (la vigente si el
    /// objeto se reescribió por una actualización incremental) y devuelve
    /// el texto entre esa cabecera y el "endobj" que le sigue, o null si no
    /// se encuentra ninguna de las dos cosas.
    /// </summary>
    private static string? BuscarUltimoCuerpoDeObjeto(string texto, long numero, long generacion)
    {
        var patronCabecera = new Regex($@"(?<![0-9]){numero}\s+{generacion}\s+obj\b", RegexOptions.None, TimeoutBusquedaTextual);

        Match? ultimaCabecera = null;
        foreach (Match m in patronCabecera.Matches(texto))
            ultimaCabecera = m;
        if (ultimaCabecera is null) return null;

        var inicioCuerpo = ultimaCabecera.Index + ultimaCabecera.Length;
        var finCuerpo = texto.IndexOf("endobj", inicioCuerpo, StringComparison.Ordinal);
        return finCuerpo < 0 ? null : texto[inicioCuerpo..finCuerpo];
    }

    /// <summary>Página A4 con la imagen centrada y ajustada al área imprimible, sin recortarla ni deformarla.</summary>
    private static byte[] ConvertirImagenAPdf(byte[] imagen)
    {
        // ANTES de decodificar: el coste de abrir una imagen lo fijan sus
        // dimensiones, no su tamaño en disco. Medido aquí, un PNG de 136 KB de
        // 12000 x 12000 hacía reservar 789 MB — factor 5800 — y pasaba sin que
        // nada lo mirase. El tope de 10 MB de la subida no acota esto, y el
        // presupuesto del lote tampoco: cuenta bytes de fichero, y el daño
        // está en los píxeles declarados.
        if (!DimensionesImagen.EstaDentroDelLimite(imagen))
            throw new InvalidDataException(
                "La imagen declara más píxeles de los admitidos y no se puede procesar.");

        using var documento = new PdfDocument();
        var pagina = documento.AddPage();
        using var graficos = XGraphics.FromPdfPage(pagina);
        using var flujoImagen = new MemoryStream(imagen);
        using var xImagen = XImage.FromStream(flujoImagen);

        var anchoDisponible = pagina.Width.Point - 2 * MargenPuntos;
        var altoDisponible = pagina.Height.Point - 2 * MargenPuntos;
        var anchoImagen = xImagen.PixelWidth * 72.0 / xImagen.HorizontalResolution;
        var altoImagen = xImagen.PixelHeight * 72.0 / xImagen.VerticalResolution;

        var factor = Math.Min(1.0, Math.Min(anchoDisponible / anchoImagen, altoDisponible / altoImagen));
        var anchoFinal = anchoImagen * factor;
        var altoFinal = altoImagen * factor;
        var x = MargenPuntos + (anchoDisponible - anchoFinal) / 2;
        var y = MargenPuntos + (altoDisponible - altoFinal) / 2;

        graficos.DrawImage(xImagen, x, y, anchoFinal, altoFinal);

        using var salida = new MemoryStream();
        documento.Save(salida);
        return salida.ToArray();
    }
}
