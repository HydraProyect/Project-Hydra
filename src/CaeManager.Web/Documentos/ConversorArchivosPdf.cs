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
    /// largo de un historial de reconocimientos médicos se acerca— y acota
    /// el daño de un PDF fabricado a propósito, igual que el tope de 50 MP
    /// acota el de una imagen fabricada.
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

            using var flujo = new MemoryStream(contenidoPdf);
            using var documentoOrigen = PdfReader.Open(flujo, PdfDocumentOpenMode.Import);

            // ANTES de copiar ninguna página: ver el comentario de
            // MaximoPaginasCombinadas. La cuenta es sobre el combinado
            // completo, no por archivo — varios PDFs moderados también
            // pueden sumar la bomba entre todos.
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
